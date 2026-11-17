// Teams captions: finds the live captions new Teams shows and keeps a transcript of them.
// Shared by Save-TeamsCaptions.ps1 and the Teams Captions app. Kept to C# 5 so Windows
// PowerShell 5.1 can compile it. Needs UIAutomationClient and UIAutomationTypes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace TeamsCaptions
{
    // Plain Win32 window checks. None of these wait on the other process, unlike UI
    // Automation, which can block while a window is busy, starting up or closing.
    public static class NativeWindow
    {
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        // Reading a closed Chromium window through UI Automation can quietly return an empty
        // tree instead of failing, so the saver checks the window itself.
        public static bool IsShown(IntPtr hWnd) { return IsWindow(hWnd) && IsWindowVisible(hWnd); }

        // For another process's window this returns the stored caption without messaging it.
        public static string Title(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // Visible top-level windows that belong to the given processes.
        public static List<IntPtr> VisibleWindowsOf(ICollection<int> processIds)
        {
            var found = new List<IntPtr>();
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (processIds.Contains((int)pid) && IsWindowVisible(hWnd)) found.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }

    // UI Automation reads run on a worker thread with a time limit, so a Teams window that
    // stops answering can't stall the saver. A read that times out counts as the captions
    // going away.
    public static class Uia
    {
        // Calls that timed out are left to finish on their own threads; refuse new calls
        // while three are still out, so a hung window can't pile up threads.
        private static int abandoned;

        public static T Run<T>(Func<T> call, int timeoutMs)
        {
            if (Volatile.Read(ref abandoned) >= 3) throw new TimeoutException("Earlier UI Automation calls are still waiting for an answer.");
            Task<T> task = Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                if (!task.Wait(timeoutMs))
                {
                    Interlocked.Increment(ref abandoned);
                    task.ContinueWith(delegate (Task<T> t)
                    {
                        Interlocked.Decrement(ref abandoned);
                        if (t.Exception != null) t.Exception.Handle(delegate { return true; });   // observed, so it isn't rethrown later
                    });
                    throw new TimeoutException("UI Automation gave no answer within " + timeoutMs + " ms.");
                }
            }
            catch (AggregateException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();   // keeps the original stack trace
                throw;
            }
            return task.Result;
        }

        public static AutomationElement Fetch(AutomationElement element, CacheRequest request, int timeoutMs)
        {
            return Run<AutomationElement>(delegate { return element.GetUpdatedCache(request); }, timeoutMs);
        }
    }

    public enum WatchState { Waiting, Saving, Away, Stopped }

    // What the watcher is doing. A new snapshot is made on every change, so another thread
    // can read it safely.
    public sealed class WatchStatus
    {
        public WatchState State;
        public string Title;     // meeting being saved, or null
        public string Path;      // its transcript, or null
        public int Lines;
        public string LastPath;  // the last transcript closed, or null
        public Entry[] Recent;   // copies of the end of the current transcript, or of the last one
    }

    // Call Poll() about once a second, always from the same thread. It finds the captions,
    // reads them, merges them into the transcript and saves it, and closes the transcript
    // when the captions have been gone for GraceSeconds.
    public sealed class CaptionWatcher
    {
        public const string DefaultProcessName = "ms-teams";
        public const int DefaultGraceSeconds = 180;

        // The named lock that keeps one saver per process name; the app and the script share it.
        public static string LockName(string processName) { return @"Local\TeamsCaptionsSaver-" + processName; }

        public string OutDir = TranscriptFile.DefaultFolder;
        public string ProcessName = DefaultProcessName;
        public string WindowTitle = "*";        // wildcard on the window title
        public int GraceSeconds = DefaultGraceSeconds;
        public int SaveEverySeconds = 5;
        public bool Watch = true;               // false: finish after the first meeting
        public string RawLogPath;               // optional troubleshooting log
        public int UiaTimeoutMs = 10000;
        public int RecentLines = 200;

        public event Action<string> Message;                   // status lines for people
        public event Action<string> Trace;                     // errors that were handled, slow reads
        public event Action<WatchStatus> Changed;
        public event Action<string, string> SessionStarted;    // title, transcript path
        public event Action<string, string, int> SessionSaved; // title, path, lines; on close

        // One caption row: the speaker's name, then the words.
        private const string RowClass = "ChatMessageCompact__body";

        private readonly CacheRequest scanRequest;
        private readonly CacheRequest rowRequest;
        private readonly Dictionary<IntPtr, DateTime> nextScan = new Dictionary<IntPtr, DateTime>();
        private Attachment attached;
        private Session session;
        private DateTime nextSearch = DateTime.MinValue;
        private string lastSig;
        private int emptyReads;
        private bool stopped;
        private bool finished;
        private string lastPath;
        private Entry[] lastRecent = new Entry[0];
        private volatile WatchStatus status;

        public CaptionWatcher()
        {
            scanRequest = NewRequest(AutomationElement.NameProperty, AutomationElement.ClassNameProperty,
                AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty);
            rowRequest = NewRequest(AutomationElement.NameProperty, AutomationElement.ClassNameProperty,
                AutomationElement.ControlTypeProperty);
            status = new WatchStatus { State = WatchState.Waiting, Recent = lastRecent };
        }

        public bool Finished { get { return finished; } }

        public WatchStatus Status { get { return status; } }

        public void Poll(DateTime now)
        {
            if (stopped || finished) return;
            bool changed = false;

            if (attached == null && now >= nextSearch)
            {
                nextSearch = now.AddSeconds(2);
                Attachment found = FindPanel(now);
                if (found != null)
                {
                    if (session != null && !SameMeeting(found, now)) CloseSession(now);
                    if (session != null)
                    {
                        session.Log.Break();
                        if (RawLogPath != null) RawLog.Break(RawLogPath, now, session.Title);
                        Say("Captions are back; carrying on in " + session.Path);
                    }
                    else
                    {
                        session = new Session(found.Title, TranscriptFile.NewPath(OutDir, found.Title, now), now);
                        Say("Saving captions from '" + session.Title + "' to " + session.Path);
                        if (SessionStarted != null) SessionStarted(session.Title, session.Path);
                    }
                    attached = found;
                    lastSig = null;
                    emptyReads = 0;
                    changed = true;
                }
            }

            if (attached != null)
            {
                // Gone: the window closed or hid, reads fail, or three reads in a row came back empty.
                List<string> speakers = null, texts = null;
                bool gone = !NativeWindow.IsShown(attached.Hwnd);
                if (!gone)
                {
                    try { ReadScreen(now, out speakers, out texts); }
                    catch (Exception ex)
                    {
                        if (!IsGone(ex)) throw;
                        TraceProblem("reading captions", ex);
                        gone = true;
                    }
                }
                if (!gone)
                {
                    if (texts.Count > 0)
                    {
                        emptyReads = 0;
                        session.LastSeen = now;
                        string[] sp = speakers.ToArray(), tx = texts.ToArray();
                        string sig = string.Join("\n", sp) + "\r" + string.Join("\n", tx);
                        if (sig != lastSig)
                        {
                            lastSig = sig;
                            if (RawLogPath != null) RawLog.Screen(RawLogPath, now, session.Title, sp, tx);
                            if (session.Log.Merge(sp, tx, now) > 0) { session.Dirty = true; changed = true; }
                        }
                    }
                    else
                    {
                        emptyReads++;
                        if (emptyReads >= 3) gone = true;
                        attached.Recheck = now;   // look for the panel afresh on the next read
                    }
                }
                if (gone)
                {
                    attached = null;
                    Say("The captions went away (pop-out closed, captions off or meeting over). Watching for them to come back.");
                    changed = true;
                }
            }

            if (session != null)
            {
                if (session.Dirty && (now - session.SavedAt).TotalSeconds >= SaveEverySeconds) Save(session, now);
                if (attached == null && (now - session.LastSeen).TotalSeconds >= GraceSeconds)
                {
                    CloseSession(now);
                    changed = true;
                    if (!Watch) finished = true;
                    else Say("Waiting for the next meeting with live captions.");
                }
            }

            if (changed) Publish();
        }

        // Saves and closes the current transcript and stops reading until Start().
        public void Stop()
        {
            if (session != null) CloseSession(DateTime.Now);
            attached = null;
            stopped = true;
            Publish();
        }

        public void Start()
        {
            stopped = false;
            finished = false;
            nextSearch = DateTime.MinValue;
            nextScan.Clear();
            Publish();
        }

        // Saves and closes the current transcript, if any (for exit).
        public void Shutdown()
        {
            if (session != null) CloseSession(DateTime.Now);
        }

        private void Say(string message)
        {
            if (Message != null) Message(message);
        }

        private void TraceProblem(string doing, Exception ex)
        {
            TraceProblem(doing + ": " + ex.GetType().Name + ": " + ex.Message);
        }

        private void TraceProblem(string message)
        {
            if (Trace != null) Trace(message);
        }

        // Captions that come back within the grace period belong to the same meeting if their
        // window has the same meeting name, or if they show lines this transcript already has:
        // the pop-out, a pinned pop-out and Teams in another language name windows differently.
        private bool SameMeeting(Attachment found, DateTime now)
        {
            if ((now - session.LastSeen).TotalSeconds > GraceSeconds) return false;
            if (found.Title == session.Title) return true;
            try
            {
                List<string> speakers, texts;
                ReadRows(found.Panel, out speakers, out texts);
                return session.Log.Overlap(speakers.ToArray(), texts.ToArray()) >= 2;
            }
            catch (Exception ex)
            {
                if (!IsGone(ex)) throw;
                TraceProblem("reading captions", ex);
                return false;
            }
        }

        // Reads the rows on screen. Every 5 seconds, and after an empty read, it first finds the
        // panel again in its window: Chromium can swap out the elements under a reference we
        // hold, which then keep returning old rows. A window with no panel any more reads empty.
        private void ReadScreen(DateTime now, out List<string> speakers, out List<string> texts)
        {
            Stopwatch timer = Stopwatch.StartNew();
            if (now >= attached.Recheck)
            {
                PanelSearch again = FindPanelInTree(Uia.Fetch(attached.Window, scanRequest, UiaTimeoutMs));
                attached.Recheck = now.AddSeconds(5);
                if (again.Panel == null)
                {
                    speakers = new List<string>();
                    texts = new List<string>();
                    return;
                }
                attached.Panel = again.Panel;
            }
            ReadRows(attached.Panel, out speakers, out texts);
            if (timer.ElapsedMilliseconds > 1000) TraceProblem("reading captions took " + timer.ElapsedMilliseconds + " ms");
        }

        private void Save(Session s, DateTime now)
        {
            if (s.Log.Count > 0)
            {
                try
                {
                    TranscriptFile.Write(s.Path, s.Log, s.Title);
                    s.Dirty = false;
                }
                catch (IOException ex) { Say("Could not save yet, will retry: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Say("Could not save yet, will retry: " + ex.Message); }
            }
            s.SavedAt = now;
        }

        private void CloseSession(DateTime now)
        {
            Session s = session;
            session = null;
            if (s.Log.Count == 0)
            {
                Say("No captions were saved for that meeting.");
                return;
            }
            string path = SaveLast(s);
            if (path == null) return;
            Say("Saved " + s.Log.Count + " lines to " + path);
            lastPath = path;
            lastRecent = s.Log.Tail(RecentLines);
            if (SessionSaved != null) SessionSaved(s.Title, path, s.Log.Count);
        }

        // The last save of a transcript. If its file can't be written (open and locked in
        // another program, say), the lines go to a copy beside it, or failing that to the
        // app's own folder, rather than being lost. Returns where they went, or null.
        private string SaveLast(Session s)
        {
            string name = Path.GetFileNameWithoutExtension(s.Path);
            string[] places =
            {
                s.Path,
                Path.Combine(Path.GetDirectoryName(s.Path), name + " (copy).md"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Teams Captions", "Unsaved", name + ".md")
            };
            Exception problem = null;
            foreach (string place in places)
            {
                try
                {
                    TranscriptFile.Write(place, s.Log, s.Title);
                    if (problem != null) Say("Could not write " + s.Path + " (" + problem.Message + "), so the transcript went to " + place);
                    return place;
                }
                catch (IOException ex) { problem = ex; }
                catch (UnauthorizedAccessException ex) { problem = ex; }
                catch (ArgumentException ex) { problem = ex; }
                catch (NotSupportedException ex) { problem = ex; }
            }
            Say("Could not save " + s.Log.Count + " lines from '" + s.Title + "': " + problem.Message);
            return null;
        }

        private void Publish()
        {
            var s = new WatchStatus();
            s.State = stopped ? WatchState.Stopped
                : session == null ? WatchState.Waiting
                : attached != null ? WatchState.Saving
                : WatchState.Away;
            s.Title = session != null ? session.Title : null;
            s.Path = session != null ? session.Path : null;
            s.Lines = session != null ? session.Log.Count : 0;
            s.LastPath = lastPath;
            s.Recent = session != null ? session.Log.Tail(RecentLines) : lastRecent;
            status = s;
            if (Changed != null) Changed(s);
        }

        // Looks through the Teams windows for live captions. Big windows (the main Teams
        // window) are checked less often, so waiting costs little.
        private Attachment FindPanel(DateTime now)
        {
            var pids = new List<int>();
            foreach (Process p in Process.GetProcessesByName(ProcessName))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
            if (pids.Count == 0)
            {
                nextScan.Clear();
                return null;
            }
            // Windows are listed and named through Win32, which never waits on Teams; only
            // the windows worth reading go through UI Automation, with a time limit.
            List<IntPtr> windows = NativeWindow.VisibleWindowsOf(pids);
            var closed = new List<IntPtr>();
            foreach (IntPtr known in nextScan.Keys) if (!windows.Contains(known)) closed.Add(known);
            foreach (IntPtr known in closed) nextScan.Remove(known);

            foreach (IntPtr hwnd in windows)
            {
                DateTime next;
                if (nextScan.TryGetValue(hwnd, out next) && now < next) continue;
                string name = NativeWindow.Title(hwnd);
                if (!Like(name, WindowTitle)) { nextScan[hwnd] = now.AddSeconds(10); continue; }
                try
                {
                    Stopwatch timer = Stopwatch.StartNew();
                    IntPtr handle = hwnd;
                    AutomationElement window = Uia.Run<AutomationElement>(delegate { return AutomationElement.FromHandle(handle); }, UiaTimeoutMs);
                    PanelSearch found = FindPanelInTree(Uia.Fetch(window, scanRequest, UiaTimeoutMs));
                    if (timer.ElapsedMilliseconds > 1000) TraceProblem("reading window '" + name + "' took " + timer.ElapsedMilliseconds + " ms (" + found.Nodes + " elements)");
                    if (found.Panel != null)
                    {
                        return new Attachment
                        {
                            Panel = found.Panel,
                            Window = window,
                            Hwnd = hwnd,
                            Title = TranscriptFile.MeetingTitle(name),
                            Recheck = now.AddSeconds(5)
                        };
                    }
                    nextScan[hwnd] = now.AddSeconds(found.CaptionsOn ? 2 : found.Nodes > 600 ? 30 : 6);
                }
                catch (Exception ex)
                {
                    if (!IsGone(ex)) throw;
                    TraceProblem("reading window '" + name + "'", ex);
                    nextScan[hwnd] = now.AddSeconds(6);
                }
            }
            return null;
        }

        private sealed class PanelSearch
        {
            public AutomationElement Panel;
            public bool CaptionsOn;
            public int Nodes;
        }

        // Finds the captions panel in one window's cached tree. The caption controls
        // (settings, feedback) have automation ids starting "captions-"; the panel is their
        // nearest ancestor that holds caption rows. Fallback: rows under a container whose
        // name mentions captions.
        private static PanelSearch FindPanelInTree(AutomationElement tree)
        {
            var result = new PanelSearch();
            AutomationElement anchor = null, topRow = null;
            foreach (AutomationElement e in Walk(tree, IsRow))
            {
                result.Nodes++;
                if (anchor == null && StartsWith(e.Cached.AutomationId, "captions-")) anchor = e;
                if (topRow == null && IsRow(e)) topRow = e;
            }
            result.CaptionsOn = anchor != null;
            if (anchor != null)
            {
                AutomationElement p = Parent(anchor);
                for (int i = 0; p != null && result.Panel == null && i < 8; i++)
                {
                    if (HasRows(p)) result.Panel = p;
                    else p = Parent(p);
                }
            }
            if (result.Panel == null && topRow != null)
            {
                AutomationElement p = Parent(topRow);
                for (int i = 0; p != null && result.Panel == null && i < 10; i++)
                {
                    if (Contains(p.Cached.Name, "caption")) result.Panel = p;
                    else p = Parent(p);
                }
            }
            return result;
        }

        private static bool HasRows(AutomationElement element)
        {
            foreach (AutomationElement e in Walk(element, IsRow)) if (IsRow(e)) return true;
            return false;
        }

        // Reads the caption rows under the panel, top to bottom. The first text in a row is
        // the speaker, the rest the words. A row with only a name has no words yet; it is
        // picked up on a later read.
        private void ReadRows(AutomationElement panel, out List<string> speakers, out List<string> texts)
        {
            speakers = new List<string>();
            texts = new List<string>();
            AutomationElement tree = Uia.Fetch(panel, rowRequest, UiaTimeoutMs);
            foreach (AutomationElement row in Walk(tree, IsRow))
            {
                if (!IsRow(row)) continue;
                var leaves = new List<string>();
                foreach (AutomationElement e in Walk(row, IsTextLeaf))
                {
                    if (!IsTextLeaf(e)) continue;
                    string name = e.Cached.Name;
                    if (name != null && name.Trim().Length > 0) leaves.Add(name.Trim());
                }
                if (leaves.Count < 2) continue;
                string text = Regex.Replace(string.Join(" ", leaves.GetRange(1, leaves.Count - 1).ToArray()), @"\s+", " ").Trim();
                if (text.Length == 0) continue;
                speakers.Add(leaves[0]);
                texts.Add(text);
            }
        }

        // Every element under root, top to bottom as on screen. The children of an element
        // are skipped when skipChildren says so.
        private static IEnumerable<AutomationElement> Walk(AutomationElement root, Func<AutomationElement, bool> skipChildren)
        {
            var stack = new Stack<AutomationElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                AutomationElement e = stack.Pop();
                yield return e;
                if (skipChildren(e)) continue;
                AutomationElementCollection kids = e.CachedChildren;
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
            }
        }

        private static bool IsRow(AutomationElement e)
        {
            return Contains(e.Cached.ClassName, RowClass);
        }

        // A text with no text inside it (a text that holds texts is just a wrapper).
        private static bool IsTextLeaf(AutomationElement e)
        {
            if (e.Cached.ControlType != ControlType.Text) return false;
            foreach (AutomationElement k in e.CachedChildren)
            {
                if (k.Cached.ControlType == ControlType.Text) return false;
            }
            return true;
        }

        private static AutomationElement Parent(AutomationElement e)
        {
            try { return e.CachedParent; }
            catch (InvalidOperationException) { return null; }
        }

        // The errors UI Automation raises when a window or element has gone away. Only UI
        // Automation calls are wrapped in this check, so other errors still surface.
        private static bool IsGone(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is ElementNotAvailableException || e is COMException ||
                    e is InvalidOperationException || e is TimeoutException) return true;
            }
            return false;
        }

        private static bool StartsWith(string text, string prefix)
        {
            return text != null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string text, string part)
        {
            return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // PowerShell-style wildcard: * and ?, any case.
        private static bool Like(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
            string rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(text ?? string.Empty, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private sealed class Session
        {
            public readonly string Title;
            public readonly string Path;
            public readonly CaptionLog Log = new CaptionLog();
            public DateTime LastSeen;
            public DateTime SavedAt = DateTime.MinValue;
            public bool Dirty;

            public Session(string title, string path, DateTime now)
            {
                Title = title;
                Path = path;
                LastSeen = now;
            }
        }

        private sealed class Attachment
        {
            public AutomationElement Panel;
            public AutomationElement Window;
            public IntPtr Hwnd;
            public string Title;
            public DateTime Recheck;
        }

        private static CacheRequest NewRequest(params AutomationProperty[] properties)
        {
            var request = new CacheRequest();
            request.TreeScope = TreeScope.Subtree;
            request.TreeFilter = Automation.RawViewCondition;
            foreach (AutomationProperty p in properties) request.Add(p);
            return request;
        }
    }
}
