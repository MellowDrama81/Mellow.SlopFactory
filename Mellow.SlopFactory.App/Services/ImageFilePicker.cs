namespace Mellow.SlopFactory.App.Services;

/// <summary>A file the user picked, read into memory.</summary>
public record PickedFile(string FileName, byte[] Data);

/// <summary>
/// Lets the user pick image files with the platform's own file dialog. Used instead of the web view's file input,
/// whose dialog crashed the app on Windows (WebView2 156.0.4314.58 failing a check in EmbeddedBrowserWebView.dll
/// while the user browsed).
/// </summary>
public static class ImageFilePicker
{
	public const long MaxFileSize = 25 * 1024 * 1024;

	private static readonly FilePickerFileType ImageTypes = new(new Dictionary<DevicePlatform, IEnumerable<string>>
	{
		[DevicePlatform.WinUI] = [".png", ".jpg", ".jpeg", ".webp"],
		[DevicePlatform.Android] = ["image/png", "image/jpeg", "image/webp"],
		[DevicePlatform.iOS] = ["public.png", "public.jpeg", "org.webmproject.webp"],
		[DevicePlatform.MacCatalyst] = ["public.png", "public.jpeg", "org.webmproject.webp"],
	});

	/// <summary>
	/// Shows the dialog and reads the chosen images. Returns nothing if the user cancels. Files over
	/// <see cref="MaxFileSize"/> or that can't be read are left out and described in Problems.
	/// </summary>
	public static async Task<(IReadOnlyList<PickedFile> Files, IReadOnlyList<string> Problems)> PickAsync()
	{
		var results = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Add images", FileTypes = ImageTypes });
		var files = new List<PickedFile>();
		var problems = new List<string>();
		foreach (var result in results ?? [])
		{
			if (result == null)
			{
				continue;
			}

			try
			{
				await using var stream = await result.OpenReadAsync();
				if (stream.CanSeek && stream.Length > MaxFileSize)
				{
					problems.Add($"{result.FileName} is larger than {MaxFileSize / (1024 * 1024)} MB");
					continue;
				}

				using var buffer = new MemoryStream();
				await stream.CopyToAsync(buffer);
				if (buffer.Length > MaxFileSize)
				{
					problems.Add($"{result.FileName} is larger than {MaxFileSize / (1024 * 1024)} MB");
					continue;
				}

				files.Add(new PickedFile(result.FileName, buffer.ToArray()));
			}
			catch (Exception ex)
			{
				problems.Add($"{result.FileName}: {ex.Message}");
			}
		}

		return (files, problems);
	}
}
