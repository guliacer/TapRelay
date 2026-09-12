using System;
using System.Collections.Generic;

namespace TapRelay
{
    /// <summary>
    /// A data-only adapter definition. Remote catalogs never contain executable code.
    /// </summary>
    public sealed class RemoteAdapterRule
    {
        // The native parser is a fixed, allow-listed extension for formats that
        // cannot be represented safely by one regular expression.
        internal const string WorkBuddyAcpParser = "workbuddy-acp";
        internal const string LeigodAppKey = "leigod";
        internal const string LeigodMiniProgramUrlVariable = "TAPRELAY_LEIGOD_MINIPROGRAM_URL";
        internal const string LeigodClientLifecycleMode = "leigod-client";

        public string AppKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string BrandColor { get; set; } = string.Empty;
        public string IconBase64 { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public string Parser { get; set; } = string.Empty;
        public bool RequiresProgramUpdate { get; set; }
        public List<string> LogPaths { get; set; } = new List<string>();
        // Ordinary remote adapters must declare the process names that make
        // their log stream active. Close-reminder rules use the lifecycle
        // fields below instead and intentionally do not need this gate.
        public List<string> ProcessNames { get; set; } = new List<string>();
        public bool Recursive { get; set; }
        public string LinePattern { get; set; } = string.Empty;
        public Dictionary<string, string> StatusMap { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string TaskIdGroup { get; set; } = "taskId";
        public string StatusGroup { get; set; } = "status";
        public string MessageGroup { get; set; } = "message";
        public string ProjectNameGroup { get; set; } = string.Empty;
        public string CwdGroup { get; set; } = string.Empty;
        public string DurationMsGroup { get; set; } = string.Empty;

        // A close reminder is deliberately declarative. The PC binary owns the
        // lifecycle implementation; the catalog only supplies matching metadata
        // and the user-visible reminder payload.
        public bool CloseReminderEnabled { get; set; }
        public List<string> ImageNames { get; set; } = new List<string>();
        public List<string> InstallRoots { get; set; } = new List<string>();
        public bool NotifyOnSessionEnding { get; set; } = true;
        public string CloseReminderTitle { get; set; } = string.Empty;
        public string CloseReminderMessage { get; set; } = string.Empty;
        public string ActionUrl { get; set; } = string.Empty;
        public string LifecycleMode { get; set; } = string.Empty;

        internal string Fingerprint { get; set; } = string.Empty;
    }
}
