using System.Text.Json;
using MediaDownloader.Services.Security;

namespace MediaDownloader.Services.Api;

/// <summary>
/// Publishes the app's resolved agent endpoint after Kestrel has started. The preferred port can
/// move when it is already occupied, so clients and the Settings page must not guess it.
/// </summary>
public sealed class AgentEndpointInfo
{
    private readonly object _gate = new();
    private string _baseUrl = "http://localhost:47820";
    private string _token = string.Empty;

    public string BaseUrl
    {
        get { lock (_gate) return _baseUrl; }
    }

    public string ApiUrl => BaseUrl + "/api";
    public string McpUrl => BaseUrl + "/mcp";

    public void Publish(string baseUrl, string token)
    {
        lock (_gate)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _token = token;
            WriteFile();
        }
    }

    public void UpdateToken(string token)
    {
        lock (_gate)
        {
            _token = token;
            WriteFile();
        }
    }

    private void WriteFile()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(new
        {
            baseUrl = _baseUrl,
            apiUrl = _baseUrl + "/api",
            mcpUrl = _baseUrl + "/mcp",
            token = _token
        }, new JsonSerializerOptions { WriteIndented = true });

        var tempPath = AppPaths.AgentEndpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var stream = new FileStream(tempPath, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(json);

            // Lock down the complete temporary file before it becomes discoverable at the stable
            // path, then re-assert after replacement for platforms whose move semantics retain the
            // destination ACL. This also prevents clients from reading half-written JSON.
            SecureFilePermissions.RestrictToCurrentUser(tempPath);
            File.Move(tempPath, AppPaths.AgentEndpointPath, overwrite: true);
            SecureFilePermissions.RestrictToCurrentUser(AppPaths.AgentEndpointPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
