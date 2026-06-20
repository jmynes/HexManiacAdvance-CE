using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HavenSoft.HexManiac.Mcp;

// Entry point for the HexManiacAdvance MCP server.
// Communicates over stdio (JSON-RPC) with an MCP client such as Claude Code.
//
// IMPORTANT: nothing may be written to stdout except the MCP protocol stream,
// or the client will fail to parse messages. All logging goes to stderr.
public static class Program {
   public static async Task Main(string[] args) {
      // HexManiac.Core resolves its "resources/" folder relative to the current
      // working directory. An MCP client may launch us from anywhere, so anchor
      // the working directory to the folder containing this executable (where the
      // build copies resources/) before any model is constructed.
      Directory.SetCurrentDirectory(AppContext.BaseDirectory);

      var builder = Host.CreateDefaultBuilder(args)
         .ConfigureLogging(logging => {
            logging.ClearProviders();
            // Logs to stderr keep stdout clean for the stdio transport.
            logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
         })
         .ConfigureServices(services => {
            // One ROM session is shared across all tool calls for the process lifetime.
            services.AddSingleton<RomSession>();

            services
               .AddMcpServer()
               .WithStdioServerTransport()
               .WithToolsFromAssembly()
               .WithResourcesFromAssembly();
         });

      await builder.Build().RunAsync();
   }
}
