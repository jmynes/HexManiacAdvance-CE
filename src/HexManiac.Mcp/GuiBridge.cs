using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

// Three-way outcome of a pipe call.
public enum GuiCallResult { Unreachable, Ok, Error }

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

   // Sends one request to the GUI pipe and reports one of three outcomes:
   //   Ok          – GUI returned a success response; resultJson holds the inner object.
   //   Error       – GUI was reachable but returned an error; error holds the message.
   //   Unreachable – connect phase failed (timeout / no pipe); error holds ex.Message.
   //
   // Separating the connect phase from the I/O phase lets callers distinguish a
   // "GUI is down" fallback from a real live error that must not be swallowed.
   public static GuiCallResult TryCall(string method, object @params, out string resultJson, out string error) {
      resultJson = "";
      error = "";
      NamedPipeClientStream? c = null;
      try {
         c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
         // Short timeout: when a GUI is up the localhost pipe connects in ~1ms;
         // when it's down we want to fall back to headless quickly.
         c.Connect(250);
         // Past this point the GUI is reachable; any subsequent failure is an Error,
         // not Unreachable, so callers must not fall through to the headless path.
      } catch (System.Exception ex) {
         c?.Dispose();
         error = ex.Message;
         return GuiCallResult.Unreachable;
      }

      // GUI is reachable — perform the request/response exchange.
      try {
         // leaveOpen so disposing the reader/writer doesn't close the pipe twice
         // (a double-close throws "Cannot access a closed pipe").
         using var w = new StreamWriter(c, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
         using var r = new StreamReader(c, Encoding.UTF8, false, 1024, leaveOpen: true);

         var req = new AutoRequest(method, JsonSerializer.SerializeToElement(@params));
         w.WriteLine(JsonSerializer.Serialize(req));

         var line = r.ReadLine();
         if (line == null) { error = "no response from GUI"; return GuiCallResult.Error; }
         var resp = JsonSerializer.Deserialize<AutoResponse>(line)!;
         if (!resp.Ok) { error = resp.Error ?? "GUI error"; return GuiCallResult.Error; }
         resultJson = resp.Result?.GetRawText() ?? "{}";
         return GuiCallResult.Ok;
      } catch (System.Exception ex) {
         error = ex.Message;
         return GuiCallResult.Error;
      } finally {
         c.Dispose();
      }
   }
}
