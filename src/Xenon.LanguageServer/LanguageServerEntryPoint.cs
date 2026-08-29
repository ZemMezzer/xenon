namespace Xenon.LanguageServer;

public static class LanguageServerEntryPoint
{
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return await new LspServerHost(input, output, error).RunAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"fatal: cannot start Xenon language server: {exception.Message}");
            return 1;
        }
    }
}
