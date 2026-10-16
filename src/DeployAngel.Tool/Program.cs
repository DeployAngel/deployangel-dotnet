using System;
using System.Text;

namespace DeployAngel.Tool;

internal static class Program
{
    private static int Main(string[] args)
    {
        // The command prints ✓, ·, and ×, and the MCP server speaks UTF-8
        // JSON, whatever the console's code page.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = utf8;
        Console.InputEncoding = utf8;
        return new Cli(args).Run();
    }
}
