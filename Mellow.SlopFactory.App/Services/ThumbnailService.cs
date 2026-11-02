using Microsoft.JSInterop;

namespace Mellow.SlopFactory.App.Services;

/// <summary>
/// Makes small JPEG thumbnails using the WebView's image decoder (wwwroot/js/thumbnails.js).
/// </summary>
public class ThumbnailService
{
	public const int MaxSize = 320;

	private readonly IJSRuntime js;

	public ThumbnailService(IJSRuntime js)
	{
		this.js = js;
	}

	/// <summary>Returns the thumbnail, or null if the image couldn't be decoded.</summary>
	public async Task<byte[]?> CreateAsync(byte[] data, string contentType)
	{
		try
		{
			return await js.InvokeAsync<byte[]>("slopFactory.makeThumbnail", data, contentType, MaxSize);
		}
		catch (JSException)
		{
			return null;
		}
	}
}
