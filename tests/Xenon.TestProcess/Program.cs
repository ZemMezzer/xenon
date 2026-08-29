using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);

// Portable runner probes and isolated C ABI checks; never load test libraries in testhost.
if (args[0] == "echo")
{
    Console.Write(args[1]); Console.Error.Write(args[2]); return int.Parse(args[3]);
}
if (args[0] == "flood")
{
    for (int i = 0; i < 256; i++) { Console.Out.Write(new string('o', 4096)); Console.Error.Write(new string('e', 4096)); }
    return 0;
}
if (args[0] == "pattern")
{
    // Independent stream sizes and recognizable content across read-buffer/ring boundaries.
    await Task.WhenAll(WritePattern(Console.Out, int.Parse(args[1]), 'A'),
        WritePattern(Console.Error, int.Parse(args[2]), 'a'));
    return 0;
}
if (args[0] == "wait")
{
    Console.WriteLine("ready"); Console.Out.Flush();
    await Task.Delay(Timeout.Infinite); return 0;
}
if (args[0] == "tree")
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("wait");
    using Process child = Process.Start(start)!;
    File.WriteAllText(args[1], child.Id.ToString());
    await Task.WhenAll(child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()),
        child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()), Task.Delay(Timeout.Infinite));
    return 0;
}
if (args[0] == "export")
{
    nint library = NativeLibrary.Load(args[1]);
    try
    {
        var function = Marshal.GetDelegateForFunctionPointer<Add>(NativeLibrary.GetExport(library, args[2]));
        return function(20, 22) == 42 ? 0 : 1;
    }
    finally { NativeLibrary.Free(library); }
}
return 2;

static async Task WritePattern(TextWriter writer, int length, char start)
{
    var buffer = new char[4093];
    for (int offset = 0; offset < length;)
    {
        int count = Math.Min(buffer.Length, length - offset);
        for (int index = 0; index < count; index++) buffer[index] = (char)(start + (offset + index) % 26);
        await writer.WriteAsync(buffer.AsMemory(0, count));
        offset += count;
    }
    await writer.FlushAsync();
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int Add(int left, int right);
