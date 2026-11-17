// Teams captions: the transcript and its files. Shared by Save-TeamsCaptions.ps1 and the
// Teams Captions app. Kept to C# 5 so Windows PowerShell 5.1 can compile it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsCaptions
{
    // One line of the transcript.
    public sealed class Entry
    {
        public string Speaker;
        public string Text;
        public DateTime FirstSeen;   // when the line first appeared on this PC
        internal int SeenMerge;      // the last merge that found it on screen

        public Entry Copy()
        {
            var e = new Entry();
            e.Speaker = Speaker;
            e.Text = Text;
            e.FirstSeen = FirstSeen;
            return e;
        }
    }

    // The transcript so far. Merge() takes what Teams shows now (top to bottom) and works out
    // which rows are new, which are still being revised, and which scrolled away.
    public sealed class CaptionLog
    {
        // A new paragraph starts when the speaker changes or after this long from one speaker.
        public const int TurnSeconds = 120;

        // A line counts as "on screen a moment ago" for this many merges.
        private const int RecentMerges = 3;

        // Break() skips more merges than that, so nothing from before counts as recent.
        private const int BreakMerges = RecentMerges + 7;

        private readonly List<Entry> entries = new List<Entry>();
        private int merge;

        public int Count { get { return entries.Count; } }

        public IList<Entry> Entries { get { return entries.AsReadOnly(); } }

        // Copies of the last lines, for showing while the log keeps changing.
        public Entry[] Tail(int count)
        {
            int from = Math.Max(0, entries.Count - count);
            var tail = new Entry[entries.Count - from];
            for (int i = from; i < entries.Count; i++) tail[i - from] = entries[i].Copy();
            return tail;
        }

        public static bool StartsTurn(Entry head, Entry line)
        {
            return head == null || line.Speaker != head.Speaker || (line.FirstSeen - head.FirstSeen).TotalSeconds > TurnSeconds;
        }

        // Returns the number of lines added, changed or removed.
        public int Merge(string[] speakers, string[] texts, DateTime now)
        {
            if (speakers == null || texts == null || speakers.Length == 0 || speakers.Length != texts.Length) return 0;
            int m = speakers.Length;
            merge++;

            // Rows that match a stored line exactly are anchors. Look near the end first;
            // fall back to the whole log if the screen shows older rows (scrolled up).
            int start = Math.Max(0, entries.Count - (m + 30));
            List<int[]> pairs = Lcs(start, speakers, texts);
            if (start > 0 && pairs.Count * 2 < m)
            {
                start = 0;
                pairs = Lcs(0, speakers, texts);
            }
            foreach (int[] p in pairs) entries[p[0]].SeenMerge = merge;

            var bounds = new List<int[]>(pairs.Count + 2);
            bounds.Add(new[] { start - 1, -1 });
            bounds.AddRange(pairs);
            bounds.Add(new[] { entries.Count, m });

            // Fill the gaps between anchors from the end, so earlier indexes stay valid.
            int changes = 0;
            for (int g = bounds.Count - 2; g >= 0; g--)
            {
                int eFrom = bounds[g][0] + 1, eTo = bounds[g + 1][0];
                int rFrom = bounds[g][1] + 1, rTo = bounds[g + 1][1];
                if (eFrom < eTo || rFrom < rTo) changes += FillGap(eFrom, eTo, rFrom, rTo, speakers, texts, now, g > 0);
            }
            return changes;
        }

        // Call when the captions come back after going away, so that nothing from before
        // counts as "on screen a moment ago".
        public void Break() { merge += BreakMerges; }

        // How well the rows on screen match the end of this transcript: one point for each
        // row found word for word among its last 300 lines, two for a long one. Captions that
        // come back scoring 2 or more belong to the same meeting.
        public int Overlap(string[] speakers, string[] texts)
        {
            var recent = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = Math.Max(0, entries.Count - 300); i < entries.Count; i++) recent[entries[i].Speaker + "\n" + entries[i].Text] = true;
            int score = 0;
            for (int j = 0; j < speakers.Length && j < texts.Length; j++)
            {
                if (recent.ContainsKey(speakers[j] + "\n" + texts[j])) score += texts[j].Length >= 25 ? 2 : 1;
            }
            return score;
        }

        private List<int[]> Lcs(int start, string[] sp, string[] tx)
        {
            int t = entries.Count - start, m = sp.Length;
            var len = new int[t + 1, m + 1];
            for (int i = t - 1; i >= 0; i--)
            {
                Entry x = entries[start + i];
                for (int j = m - 1; j >= 0; j--)
                {
                    len[i, j] = (x.Speaker == sp[j] && x.Text == tx[j])
                        ? len[i + 1, j + 1] + 1
                        : Math.Max(len[i + 1, j], len[i, j + 1]);
                }
            }
            // Pass over a stored line whenever the rest still gives the same number of matches,
            // so a repeated line ("Yeah.") anchors to its latest copy, not one that scrolled off.
            var pairs = new List<int[]>();
            int a = 0, b = 0;
            while (a < t && b < m)
            {
                Entry x = entries[start + a];
                if (len[a + 1, b] == len[a, b]) a++;
                else if (x.Speaker == sp[b] && x.Text == tx[b]) { pairs.Add(new[] { start + a, b }); a++; b++; }
                else b++;
            }
            return pairs;
        }

        // Rows between two anchors are revisions of lines that were on screen a moment ago
        // (same speaker, and similar words or the same place) or new lines. A small alignment
        // picks the pairing. afterShown: the gap starts below a line that is on screen now.
        private int FillGap(int eFrom, int eTo, int rFrom, int rTo, string[] sp, string[] tx, DateTime now, bool afterShown)
        {
            int ne = eTo - eFrom, nr = rTo - rFrom;
            var best = new int[ne + 1, nr + 1];
            for (int i = ne - 1; i >= 0; i--)
            {
                for (int j = nr - 1; j >= 0; j--)
                {
                    int v = Math.Max(best[i + 1, j], best[i, j + 1]);
                    int s = PairScore(entries[eFrom + i], sp[rFrom + j], tx[rFrom + j]);
                    if (s > 0) v = Math.Max(v, s + best[i + 1, j + 1]);
                    best[i, j] = v;
                }
            }

            // Walk the alignment. On ties, pass over old lines first: rows then pair with the
            // later lines (those that scroll off are at the top), and old lines come before new.
            var result = new List<Entry>(ne + nr);
            int changes = 0, a = 0, b = 0;
            while (a < ne || b < nr)
            {
                if (a < ne && best[a + 1, b] == best[a, b])
                {
                    result.Add(entries[eFrom + a]);
                    a++;
                    continue;
                }
                if (a < ne && b < nr)
                {
                    Entry x = entries[eFrom + a];
                    int s = PairScore(x, sp[rFrom + b], tx[rFrom + b]);
                    if (s > 0 && best[a, b] == s + best[a + 1, b + 1])
                    {
                        if (x.Text != tx[rFrom + b]) { x.Text = tx[rFrom + b]; changes++; }
                        x.SeenMerge = merge;
                        result.Add(x);
                        a++;
                        b++;
                        continue;
                    }
                }
                Entry n = new Entry();
                n.Speaker = sp[rFrom + b];
                n.Text = tx[rFrom + b];
                n.FirstSeen = now;
                n.SeenMerge = merge;
                result.Add(n);
                changes++;
                b++;
            }

            // The transcript mirrors Teams: lines that scrolled off the top, then the screen.
            // A line from the last screen that is gone now, below a line still on screen, was
            // removed or folded away by Teams, so it goes. One above everything on screen just
            // scrolled off the top, so it stays.
            bool shown = afterShown;
            for (int i = 0; i < result.Count; i++)
            {
                Entry x = result[i];
                if (x.SeenMerge == merge) { shown = true; continue; }
                if (shown && x.SeenMerge == merge - 1)
                {
                    result.RemoveAt(i);
                    i--;
                    changes++;
                }
            }

            // Teams can slot a line in above lines it already shows (two people talking at once,
            // or older captions coming into view). Such a line was said before the line below
            // it, so it gets no later a time, and times in the transcript never run backwards.
            DateTime bound = eTo < entries.Count ? entries[eTo].FirstSeen : DateTime.MaxValue;
            for (int i = result.Count - 1; i >= 0; i--)
            {
                if (result[i].FirstSeen > bound) result[i].FirstSeen = bound;
                bound = result[i].FirstSeen;
            }

            entries.RemoveRange(eFrom, ne);
            entries.InsertRange(eFrom, result);
            return changes;
        }

        // 3: a recent line the row carries on (Teams added or took words at the end, or folded
        // the next line into it). 2: a recent line with similar words. 1: the line was on the
        // last screen. 0: no pair.
        private int PairScore(Entry x, string speaker, string text)
        {
            if (x.Speaker != speaker || x.SeenMerge >= merge) return 0;
            if (x.SeenMerge >= merge - RecentMerges)
            {
                if (CarriesOn(x.Text, text)) return 3;
                if (Similar(x.Text, text)) return 2;
            }
            return x.SeenMerge == merge - 1 ? 1 : 0;
        }

        // True when one text starts with the whole of the other (ignoring case and punctuation).
        public static bool CarriesOn(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            return x.Length > 0 && y.Length > 0 && (y.StartsWith(x, StringComparison.Ordinal) || x.StartsWith(y, StringComparison.Ordinal));
        }

        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            bool sep = false;
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (sep && sb.Length > 0) sb.Append(' ');
                    sb.Append(char.ToLowerInvariant(ch));
                    sep = false;
                }
                else sep = true;
            }
            return sb.ToString();
        }

        // True when b looks like a revision of a: a shared start, or most words in common.
        public static bool Similar(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            if (x.Length == 0 || y.Length == 0) return true;
            int n = Math.Min(x.Length, y.Length), k = 0;
            while (k < n && x[k] == y[k]) k++;
            if (k * 10 >= n * 6) return true;
            string[] wx = x.Split(' '), wy = y.Split(' ');
            string[] small = wx.Length <= wy.Length ? wx : wy;
            string[] large = wx.Length <= wy.Length ? wy : wx;
            var pool = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string w in large) { int c; pool.TryGetValue(w, out c); pool[w] = c + 1; }
            int common = 0;
            foreach (string w in small) { int c; if (pool.TryGetValue(w, out c) && c > 0) { common++; pool[w] = c - 1; } }
            return common * 10 >= small.Length * 6;
        }

        // Markdown: one paragraph per speaker turn (see StartsTurn).
        public string Render(string title, string disclaimer)
        {
            const string nl = "\r\n";
            CultureInfo inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# ").Append(title).Append(nl).Append(nl);
            if (entries.Count > 0)
            {
                DateTime from = entries[0].FirstSeen, to = entries[0].FirstSeen;
                var speakers = new List<string>();
                foreach (Entry x in entries)
                {
                    if (x.FirstSeen < from) from = x.FirstSeen;
                    if (x.FirstSeen > to) to = x.FirstSeen;
                    if (!speakers.Contains(x.Speaker)) speakers.Add(x.Speaker);
                }
                sb.Append("Teams live captions, ").Append(from.ToString("dddd yyyy-MM-dd, HH:mm", inv))
                  .Append(" to ").Append(to.ToString("HH:mm", inv)).Append('.').Append(nl).Append(nl);
                sb.Append("Speakers: ").Append(string.Join(", ", speakers.ToArray())).Append('.').Append(nl).Append(nl);
            }
            if (!string.IsNullOrEmpty(disclaimer)) sb.Append(disclaimer).Append(nl).Append(nl);
            sb.Append("---").Append(nl);
            Entry head = null;
            StringBuilder para = null;
            foreach (Entry x in entries)
            {
                if (StartsTurn(head, x))
                {
                    if (para != null) sb.Append(nl).Append(para.ToString()).Append(nl);
                    para = new StringBuilder();
                    para.Append("**").Append(x.FirstSeen.ToString("HH:mm:ss", inv)).Append(' ').Append(x.Speaker).Append(":** ").Append(x.Text);
                    head = x;
                }
                else para.Append(' ').Append(x.Text);
            }
            if (para != null) sb.Append(nl).Append(para.ToString()).Append(nl);
            return sb.ToString();
        }
    }

    // Transcript files: names, the header disclaimer, and saving without leaving a
    // half-written file.
    public static class TranscriptFile
    {
        public const string Disclaimer = "Captions are machine-generated, so expect wrong words and names. Each time is when the line first appeared on this PC; lines already on screen when saving started carry that start time.";

        public static string DefaultFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Teams captions"); }
        }

        // The meeting name from a window title: "Weekly sync | Microsoft Teams" and
        // "Weekly sync | Microsoft Teams | Pinned window" both become "Weekly sync".
        public static string MeetingTitle(string windowName)
        {
            string t = Regex.Replace(windowName ?? string.Empty, @"\s*\|\s*Microsoft Teams\b.*$", string.Empty).Trim();
            return t.Length > 0 ? t : "Teams meeting";
        }

        public static string FileName(string title)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char ch in title ?? string.Empty) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '-' : ch);
            string s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim(' ', '.');
            if (s.Length > 80) s = s.Substring(0, 80).Trim();
            return s.Length > 0 ? s : "Teams meeting";
        }

        // "<dir>\2026-11-11 1530 Weekly sync.md", or "... (2).md" and so on if that is taken.
        public static string NewPath(string dir, string title, DateTime start)
        {
            string name = start.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + " " + FileName(title);
            string path = Path.Combine(dir, name + ".md");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + " (" + i + ").md");
            return path;
        }

        public static void Write(string path, CaptionLog log, string title)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, log.Render(title, Disclaimer), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    // Optional troubleshooting log: one JSON object per screen seen, so a transcript can be
    // rebuilt (Save-TeamsCaptions.ps1 -FromRawLog) or the merge tested offline.
    public static class RawLog
    {
        public static void Screen(string path, DateTime t, string title, string[] speakers, string[] texts)
        {
            Append(path, "{\"t\":" + Json(Stamp(t)) + ",\"title\":" + Json(title) +
                ",\"speakers\":" + JsonArray(speakers) + ",\"texts\":" + JsonArray(texts) + "}");
        }

        public static void Break(string path, DateTime t, string title)
        {
            Append(path, "{\"t\":" + Json(Stamp(t)) + ",\"title\":" + Json(title) + ",\"event\":\"break\"}");
        }

        private static string Stamp(DateTime t) { return t.ToString("o", CultureInfo.InvariantCulture); }

        private static void Append(string path, string line)
        {
            File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
        }

        private static string JsonArray(string[] items)
        {
            var parts = new string[items.Length];
            for (int i = 0; i < items.Length; i++) parts[i] = Json(items[i]);
            return "[" + string.Join(",", parts) + "]";
        }

        public static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
