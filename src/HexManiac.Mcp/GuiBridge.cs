using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

// Client for the GUI's automation pipe. When a HexManiacAdvance GUI is running,
// the MCP tools forward calls here so they operate on the live open tabs.
// A fresh connection is used per call (the server re-listens after each client).
public static class GuiBridge {
   private const string PipeName = "HexManiacAdvance.Automation";

   // Cheap reachability probe.
   public static bool IsGuiRunning() {
      try {
         using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
         c.Connect(200);
         return true;
      } catch {
         return false;
      }
   }

   // Sends one request; on success returns the result JSON (the inner object).
   // Returns false (with `error`) if the GUI isn't reachable or returned an error.
   public static bool TryCall(string method, object @params, out string resultJson, out string error) {
      resultJson = "";
      error = "";
      try {
         using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
         // Short timeout: when a GUI is up the localhost pipe connects in ~1ms;
         // when it's down we want to fall back to headless quickly.
         c.Connect(250);
         // leaveOpen so disposing the reader/writer doesn't close the pipe twice
         // (a double-close throws "Cannot access a closed pipe").
         using var w = new StreamWriter(c, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
         using var r = new StreamReader(c, Encoding.UTF8, false, 1024, leaveOpen: true);

         var req = new AutoRequest(method, JsonSerializer.SerializeToElement(@params));
         w.WriteLine(JsonSerializer.Serialize(req));

         var line = r.ReadLine();
         if (line == null) { error = "no response from GUI"; return false; }
         var resp = JsonSerializer.Deserialize<AutoResponse>(line)!;
         if (!resp.Ok) { error = resp.Error ?? "GUI error"; return false; }
         resultJson = resp.Result?.GetRawText() ?? "{}";
         return true;
      } catch (System.Exception ex) {
         error = ex.Message;
         return false;
      }
   }
}
