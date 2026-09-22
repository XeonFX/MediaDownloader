# Use a Mac mini dashboard from another Mac

The web dashboard controls downloads and contains private settings. It accepts local connections.
Use an SSH tunnel to reach it over your LAN; SSH authenticates the connection and encrypts both
the web pages and Blazor's live connection. Keep the app's default loopback listener. There is no
need to enable remote agent API access, open a router port, or install an HTTPS proxy.

1. On the Mac mini, enable **System Settings → General → Sharing → Remote Login**, restricted
   to your account. Set up SSH key access from the MacBook and verify the server's host key.
2. Find the mini's dashboard port (usually `47820`):

   ```sh
   ssh your-user@your-mini.local '/usr/bin/plutil -extract baseUrl raw -o - "$HOME/Library/Application Support/MediaDownloader/endpoint.json"'
   ```

3. On the MacBook, leave this command running, replacing the remote hostname and port:

   ```sh
   ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
     -L 127.0.0.1:47821:127.0.0.1:47820 your-user@your-mini.local
   ```

4. Open <http://127.0.0.1:47821> on the MacBook. Port `47821` avoids colliding with an app
   running locally on the MacBook. To stop a foreground tunnel, press Ctrl-C.

The mini must be awake and the app must be running. Paths and folder pickers refer to the mini,
even when using its dashboard from another computer. If the app changes its listening port,
restart the tunnel using the port from `endpoint.json`. Do not share the full endpoint file:
it also contains the API access token.

## Start automatically on macOS

In **Settings → General**, enable **Start with macOS**. This registers the installed app with
a per-user LaunchAgent in `~/Library/LaunchAgents`. It starts when that Mac's user signs in, including after
a restart; it does not run before login. No Apple developer account is required.

If macOS requires approval, allow MediaDownloader in **System Settings → General → Login Items
& Extensions** on the Mac running the app, then choose **Refresh login status** in the dashboard.
The switch reads the launch-agent registration and reports launchd disable overrides. Disabling it
removes the registration for future logins without stopping the current app.
Move the app into `/Applications` before enabling it. This option is unavailable for development
runs and other platforms.

## This MacBook's managed connection

For the configured MacBook, `~/Library/LaunchAgents/com.mediadownloader.remote-tunnel.plist`
starts the tunnel when you sign in and reconnects after connection failures. Its script and log
are in `~/Library/Application Support/MediaDownloaderRemote/`. The script looks up the mini's
current dashboard port on every reconnect and verifies its existing SSH host key.

To stop it:

```sh
launchctl bootout "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.mediadownloader.remote-tunnel.plist"
```

To restart it, use `launchctl bootstrap` with the same two arguments. Move the plist out of
`~/Library/LaunchAgents` if you also want to prevent it starting at your next login.
