namespace AsyncReaderWriterLockTests {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	internal static class TestUtilities {
		internal static void Set(this TaskCompletionSource<object> tcs) {
			Task.Run(() => tcs.TrySetResult(null));
		}
	}
}
