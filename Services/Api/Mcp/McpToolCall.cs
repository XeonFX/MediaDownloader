using ModelContextProtocol;

namespace MediaDownloader.Services.Api.Mcp;

internal static class McpToolCall
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (AgentApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
