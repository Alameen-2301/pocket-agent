using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PocketAgent.Core.Mcp;

/// <summary>
/// The MCP JSON-RPC protocol engine written in Rust (rust-core), called through its C ABI.
/// Tool calls come back into C# through a callback.
/// </summary>
public sealed class RustEngine
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint InvokeTool(IntPtr name, IntPtr args, IntPtr output, nuint capacity, IntPtr context);

    // "pocket_agent_core" resolves to pocket_agent_core.dll on Windows and libpocket_agent_core.so elsewhere.
    [DllImport("pocket_agent_core", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pocket_agent_handle")]
    private static extern IntPtr Handle(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string headers,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string tools,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string instructions,
        InvokeTool callback, IntPtr context);

    [DllImport("pocket_agent_core", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pocket_agent_free")]
    private static extern void Free(IntPtr ptr);

    private readonly object _rpcLock = new();
    private readonly Func<string, string, string> _invoke;
    private readonly string _toolsJson;
    private readonly string _instructions;
    private readonly InvokeTool _callback; // kept alive for the lifetime of the engine

    /// <param name="invoke">(toolName, argumentsJson) → MCP result JSON.</param>
    public RustEngine(Func<string, string, string> invoke, string toolsJson, string instructions)
    {
        _invoke = invoke;
        _toolsJson = toolsJson;
        _instructions = instructions;
        _callback = Invoke;
    }

    /// <summary>Throws DllNotFoundException/EntryPointNotFoundException when the native library is missing.</summary>
    public static void EnsureLoaded()
    {
        var ptr = Handle("{}", "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"ping\"}", "[]", "", static (_, _, _, _, _) => 0, IntPtr.Zero);
        if (ptr != IntPtr.Zero) Free(ptr);
    }

    private nuint Invoke(IntPtr namePtr, IntPtr argsPtr, IntPtr output, nuint capacity, IntPtr context)
    {
        string result;
        try
        {
            string name = Marshal.PtrToStringUTF8(namePtr) ?? "";
            string args = Marshal.PtrToStringUTF8(argsPtr) ?? "{}";
            result = _invoke(name, args);
        }
        catch (Exception e)
        {
            result = JsonSerializer.Serialize(new
            {
                content = new object[] { new { type = "text", text = "Tool error: " + e.Message } },
                isError = true,
            });
        }
        var bytes = Encoding.UTF8.GetBytes(result);
        if ((nuint)bytes.Length > capacity) return (nuint)bytes.Length; // Rust treats this as "too large"
        Marshal.Copy(bytes, 0, output, bytes.Length);
        return (nuint)bytes.Length;
    }

    public (int Status, string Body) Process(Dictionary<string, string> headers, string body)
    {
        lock (_rpcLock)
        {
            var ptr = Handle(JsonSerializer.Serialize(headers), body, _toolsJson, _instructions, _callback, IntPtr.Zero);
            if (ptr == IntPtr.Zero) return (500, "");
            try
            {
                using var doc = JsonDocument.Parse(Marshal.PtrToStringUTF8(ptr) ?? "{}");
                var root = doc.RootElement;
                return (root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? "");
            }
            finally { Free(ptr); }
        }
    }
}
