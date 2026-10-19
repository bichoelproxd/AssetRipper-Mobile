using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;

namespace AssetRipper.Mobile;

/// <summary>Starts the AssetRipper web server inside this process, once.</summary>
internal static class AppHost
{
	private static readonly object Lock = new();
	private static bool started;

	public static int Port { get; private set; }
	public static string? StartupError { get; private set; }
	public static string BaseUrl => $"http://127.0.0.1:{Port}/";

	public static string RippedDirectory => Path.Join(LocalFileSystem.ExecutingDirectory, "Ripped");

	public static void Start(string dataDirectory)
	{
		lock (Lock)
		{
			if (started)
			{
				return;
			}
			started = true;
		}

		// Must happen before anything touches settings, logs or temp folders.
		LocalFileSystem.ExecutingDirectoryOverride = dataDirectory;
		Directory.CreateDirectory(dataDirectory);
		Logger.Add(UiLogger.Instance);

		Port = FindFreePort();
		Thread thread = new(() =>
		{
			try
			{
				WebApplicationLauncher.Launch(Port, headless: true, log: false, logPath: null);
			}
			catch (Exception ex)
			{
				StartupError = ex.ToString();
				Logger.Error("The embedded web server stopped.", ex);
			}
		}, 16 * 1024 * 1024)
		{
			IsBackground = true,
			Name = "AssetRipper.Web"
		};
		thread.Start();
	}

	public static async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
	{
		DateTime end = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < end && StartupError is null)
		{
			try
			{
				using TcpClient client = new();
				await client.ConnectAsync(IPAddress.Loopback, Port);
				return true;
			}
			catch (SocketException)
			{
				await Task.Delay(200);
			}
		}
		return false;
	}

	private static int FindFreePort()
	{
		TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}
}
