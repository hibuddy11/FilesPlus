using Microsoft.Extensions.Logging;
using Windows.UI.StartScreen;

namespace Files.App.Services
{
	/// <inheritdoc cref="IStartMenuService"/>
	internal sealed class StartMenuService : IStartMenuService
	{
		[Obsolete("See IStartMenuService for further information.")]
		public bool IsPinned(string itemPath)
		{
			var tileId = GetNativeTileId(itemPath);
			try
			{
				return SecondaryTile.Exists(tileId);
			}
			catch (Exception ex)
			{
				// SecondaryTile.Exists can throw (e.g. 0x80070490) for special items; treat as not pinned
				// instead of breaking the whole context menu build.
				App.Logger?.LogWarning(ex, "Failed to check whether the item is pinned to Start.");
				return false;
			}
		}

		/// <inheritdoc/>
		public Task<bool> IsPinnedAsync(IStorable storable)
		{
			var tileId = GetNativeTileId(storable.Id);
			try
			{
				return Task.FromResult(SecondaryTile.Exists(tileId));
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Failed to check whether the item is pinned to Start.");
				return Task.FromResult(false);
			}
		}

		/// <inheritdoc/>
		public async Task PinAsync(IStorable storable, string? displayName = null)
		{
			var tileId = GetNativeTileId(storable.Id);
			displayName ??= storable.Name;

			try
			{
				var path150x150 = new Uri("ms-appx:///Assets/tile-0-300x300.png");
				var path71x71 = new Uri("ms-appx:///Assets/tile-0-250x250.png");

				var tile = new SecondaryTile(
					tileId,
					displayName,
					storable.Id,
					path150x150,
					TileSize.Square150x150)
				{
					VisualElements =
					{
						Square71x71Logo = path71x71,
						ShowNameOnSquare150x150Logo = true
					}
				};

				WinRT.Interop.InitializeWithWindow.Initialize(tile, MainWindow.Instance.WindowHandle);

				await tile.RequestCreateAsync();
			}
			catch (Exception e)
			{
				Debug.WriteLine(tileId);
				Debug.WriteLine(e.ToString());
			}

		}

		/// <inheritdoc/>
		public async Task UnpinAsync(IStorable storable)
		{
			var startScreen = StartScreenManager.GetDefault();
			var tileId = GetNativeTileId(storable.Id);

			await startScreen.TryRemoveSecondaryTileAsync(tileId);
		}

		private static string GetNativeTileId(string id)
		{
			// Remove symbols because windows doesn't like them in the ID, and will blow up
			var str = $"folder-{new string(id.Where(c => char.IsLetterOrDigit(c)).ToArray())}";

			// If the id string is too long, Windows will throw an error, so remove every other character
			if (str.Length > 64)
				str = new string(str.Where((_, i) => i % 2 == 0).ToArray());

			return str;
		}
	}
}
