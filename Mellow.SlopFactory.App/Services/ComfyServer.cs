namespace Mellow.SlopFactory.App.Services;

public static class ComfyServer
{
	/// <summary>
	/// Identifies a ComfyUI server for remembering which files it already has: its base address, lower-cased and
	/// without a trailing slash, e.g. "https://cloud.comfy.org".
	/// </summary>
	public static string Key(Uri baseAddress)
	{
		return baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
	}
}
