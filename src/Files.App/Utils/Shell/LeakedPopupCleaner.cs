// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;

namespace Files.App.Utils.Shell
{
	/// <summary>
	/// Hides leaked WinAppSDK popup bridge windows (Microsoft.UI.Content.PopupWindowSiteBridge).
	/// A popup (menu/flyout) bridge HWND should be destroyed when the popup is dismissed;
	/// a leaked one stays on screen, fully transparent, and swallows every mouse click
	/// under it — the "everything unclickable" symptom after long-running sessions.
	/// </summary>
	internal static class LeakedPopupCleaner
	{
		private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

		/// <summary>
		/// Called on main-window activation: by then live popups are already dismissed, so any
		/// still-visible popup bridge HWND of this process is a leak. Hides it and logs it, so
		/// the leak frequency and timing can be traced back to its trigger.
		/// </summary>
		internal static void HideLeakedPopupBridges()
		{
			var currentPid = (uint)Environment.ProcessId;

			EnumWindows((hWnd, _) =>
			{
				GetWindowThreadProcessId(hWnd, out var pid);
				if (pid == currentPid && IsWindowVisible(hWnd))
				{
					var className = new StringBuilder(256);
					GetClassName(hWnd, className, className.Capacity);
					if (className.ToString().Contains("PopupWindowSiteBridge", StringComparison.Ordinal))
					{
						ShowWindow(hWnd, 0);
						App.Logger?.LogWarning($"Hidden a leaked popup bridge window 0x{hWnd:X} that was swallowing input.");
					}
				}

				return true;
			}, IntPtr.Zero);
		}
	}
}
