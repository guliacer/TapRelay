using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    public sealed class AgentApiResult
    {
        public bool Accepted { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;

        public static AgentApiResult Success(string status = "Accepted", string detail = "")
        {
            return new AgentApiResult { Accepted = true, Status = status, Detail = detail };
        }

        public static AgentApiResult Failure(string code, string status, string detail = "")
        {
            return new AgentApiResult { Accepted = false, Code = code, Status = status, Detail = detail };
        }
    }

    public sealed class AgentProbeResult
    {
        public bool Running { get; set; }
        public HashSet<string> Capabilities { get; set; } = new HashSet<string>();
        public string Detail { get; set; } = string.Empty;

        public static AgentProbeResult NotRunning(string detail = "")
        {
            return new AgentProbeResult { Running = false, Detail = detail };
        }

        public static AgentProbeResult Up(HashSet<string> capabilities, string detail = "")
        {
            return new AgentProbeResult { Running = true, Capabilities = capabilities, Detail = detail };
        }
    }

    public interface IAgentApiProxy
    {
        string AgentKey { get; }
        HashSet<string> Capabilities { get; }
        Task<AgentProbeResult> ProbeAsync(CancellationToken cancellationToken);
        Task<AgentApiResult> SendAsync(string sessionId, string text, string cwd, CancellationToken cancellationToken);
        Task<AgentApiResult> StopAsync(string sessionId, CancellationToken cancellationToken);
    }
}