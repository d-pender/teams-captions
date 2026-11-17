// Teams captions: the settings file and the legal notice. Shared by Save-TeamsCaptions.ps1
// and the Teams Captions app. Kept to C# 5 so Windows PowerShell 5.1 can compile it.
using System;
using System.Globalization;
using System.IO;

namespace TeamsCaptions
{
    // What users must accept once, before anything is saved. Raise Version when the wording
    // changes in a way people should see again.
    public static class LegalNotice
    {
        public const int Version = 1;

        public const string Title = "Before you use Teams Captions";

        public static readonly string[] Paragraphs =
        {
            "Teams Captions saves the text of what people say in Teams meetings. Saving a conversation may count as recording it, and recording laws vary: in some places, including several US states such as California and Washington, everyone in the conversation must consent first; in others, such as under the EU's GDPR, a transcript is personal data with its own rules. Workplaces often have their own policies too.",
            "Teams Captions does not notify anyone in the meeting. It is solely your responsibility, as the user, to find out which laws and policies apply to you and to everyone in the meeting, to tell the other participants and get their consent wherever the law or your organization requires it, and to store, share and delete transcripts lawfully.",
            "Don't use Teams Captions where you don't have the right to keep a record of the conversation. It is provided as is, without warranty, and the author accepts no liability for how it is used. This is not legal advice; if you're unsure, ask a lawyer."
        };
    }

    // %APPDATA%\Teams Captions\settings.ini, as Name=value lines.
    public sealed class Settings
    {
        public string OutDir;
        public bool TrayTipShown;
        public double WindowWidth;    // in device-independent units; 0 = the default
        public double WindowHeight;
        public int NoticeVersion;     // the LegalNotice version the user accepted, 0 = none
        public string NoticeAcceptedOn;
        private readonly string path;

        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Teams Captions", "settings.ini"); }
        }

        public Settings(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string name = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (name == "OutDir" && value.Length > 0) OutDir = value;
                else if (name == "TrayTipShown") TrayTipShown = value == "1";
                else if (name == "WindowWidth") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out WindowWidth);
                else if (name == "WindowHeight") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out WindowHeight);
                else if (name == "NoticeVersion") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out NoticeVersion);
                else if (name == "NoticeAcceptedOn" && value.Length > 0) NoticeAcceptedOn = value;
            }
        }

        public bool NoticeAccepted { get { return NoticeVersion >= LegalNotice.Version; } }

        public void AcceptNotice(DateTime now)
        {
            NoticeVersion = LegalNotice.Version;
            NoticeAcceptedOn = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[]
            {
                "OutDir=" + (OutDir ?? string.Empty),
                "TrayTipShown=" + (TrayTipShown ? "1" : "0"),
                "WindowWidth=" + WindowWidth.ToString("0", CultureInfo.InvariantCulture),
                "WindowHeight=" + WindowHeight.ToString("0", CultureInfo.InvariantCulture),
                "NoticeVersion=" + NoticeVersion.ToString(CultureInfo.InvariantCulture),
                "NoticeAcceptedOn=" + (NoticeAcceptedOn ?? string.Empty)
            });
        }
    }
}
