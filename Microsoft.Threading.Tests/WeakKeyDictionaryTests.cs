//-----------------------------------------------------------------------
// <copyright file="WeakKeyDictionaryTests.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading.Test {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Tests for the weak dictionary class
	/// </summary>
	[TestClass]
	public class WeakKeyDictionaryTests {
		/// <summary>
		/// Find with the same key inserted using the indexer
		/// </summary>
		[TestMethod]
		public void Indexer_ReferenceFound() {
			string k1 = "key";
			string v1 = "value";

			var dictionary = new WeakKeyDictionary<string, string>();
			dictionary[k1] = v1;

			// Now look for the same key we inserted
			string v2 = dictionary[k1];

			Assert.IsTrue(Object.ReferenceEquals(v1, v2));
			Assert.IsTrue(dictionary.ContainsKey(k1));
		}

		/// <summary>
		/// Find something not present with the indexer
		/// </summary>
		[TestMethod]
		[ExpectedException(typeof(KeyNotFoundException))]
		public void Indexer_NotFound() {
			var dictionary = new WeakKeyDictionary<string, string>();
			string value = dictionary["x"];
		}

		/// <summary>
		/// Find with the same key inserted using TryGetValue
		/// </summary>
		[TestMethod]
		public void TryGetValue_ReferenceFound() {
			string k1 = "key";
			string v1 = "value";

			var dictionary = new WeakKeyDictionary<string, string>();
			dictionary[k1] = v1;

			// Now look for the same key we inserted
			string v2;
			bool result = dictionary.TryGetValue(k1, out v2);

			Assert.IsTrue(result);
			Assert.IsTrue(Object.ReferenceEquals(v1, v2));
		}

		/// <summary>
		/// Find something not present with TryGetValue
		/// </summary>
		[TestMethod]
		public void TryGetValue_ReferenceNotFound() {
			var dictionary = new WeakKeyDictionary<string, string>();

			string v;
			bool result = dictionary.TryGetValue("x", out v);

			Assert.IsFalse(result);
			Assert.IsNull(v);
			Assert.IsFalse(dictionary.ContainsKey("x"));
		}

		/// <summary>
		/// Find a key that wasn't inserted but is equal
		/// </summary>
		[TestMethod]
		public void EqualityComparer() {
			string k1 = "key";
			string v1 = "value";

			var dictionary = new WeakKeyDictionary<string, string>();
			dictionary[k1] = v1;

			// Now look for a different but equatable key
			// Don't create it with a literal or the compiler will intern it!
			string k2 = String.Concat("k", "ey");

			Assert.IsFalse(Object.ReferenceEquals(k1, k2));

			string v2 = dictionary[k2];

			Assert.IsTrue(Object.ReferenceEquals(v1, v2));
		}

		/// <summary>
		/// Magic number size of strings to allocate for GC tests.
		/// </summary>
		private const int bigMemoryFootprintTest = 1 * 1024 * 1024;

		/// <summary>
		/// Verify dictionary doesn't hold onto keys
		/// </summary>
		[TestMethod]
		public void KeysCollectable() {
			string k1 = new string('k', bigMemoryFootprintTest);
			string v1 = new string('v', bigMemoryFootprintTest);

			// Each character is 2 bytes, so about 4MB of this should be the strings
			long memory1 = GC.GetTotalMemory(true);

			var dictionary = new WeakKeyDictionary<string, string>();
			dictionary[k1] = v1;

			k1 = null;

			long memory2 = GC.GetTotalMemory(true);

			// Key collected, should be about 2MB less
			long difference = memory1 - memory2;

			Console.WriteLine("Start {0}, end {1}, diff {2}", memory1, memory2, difference);
			Assert.IsTrue(difference > 1500000); // 2MB minus big noise allowance

			// This line is VERY important, as it keeps the GC from being too smart and collecting
			// the dictionary and its large strings because we never use them again.  
			GC.KeepAlive(dictionary);
		}

		/// <summary>
		/// Call Scavenge explicitly
		/// </summary>
		[TestMethod]
		public void ExplicitScavenge() {
			object k1 = new object();
			object v1 = new object();

			var dictionary = new WeakKeyDictionary<object, object>();
			dictionary[k1] = v1;

			Assert.AreEqual(1, dictionary.Count);

			k1 = null;
			GC.Collect();

			dictionary.Scavenge();

			Assert.AreEqual(0, dictionary.Count);
		}

		/// <summary>
		/// Growing should invoke Scavenge
		/// </summary>
		[TestMethod]
		public void ScavengeOnGrow() {
			var dictionary = new WeakKeyDictionary<object, object>();

			for (int i = 0; i < 100; i++) {
				dictionary[new Object()] = new Object();

				// Randomly collect some
				if (i == 15) {
					GC.Collect();
				}
			}

			// We should have scavenged at least once
			Console.WriteLine("Count {0}", dictionary.Count);
			Assert.IsTrue(dictionary.Count < 100);

			// Finish with explicit scavenge
			int count1 = dictionary.Count;
			int removed = dictionary.Scavenge();
			int count2 = dictionary.Count;

			Console.WriteLine("Removed {0}", removed);
			Assert.AreEqual(removed, count1 - count2);
		}

		/// <summary>
		/// Tests that the enumerator correctly lists contents, skipping over collected elements.
		/// </summary>
		[TestMethod]
		public void Enumerator() {
			object keepAlive1 = new object();
			object keepAlive2 = new object();
			object collected = new object();
			var dictionary = new WeakKeyDictionary<object, int>();
			dictionary[keepAlive1] = 0;
			dictionary[collected] = 1;
			dictionary[keepAlive2] = 2;
			collected = null;
			GC.Collect();

			var enumeratedContents = dictionary.ToList();
			Assert.AreEqual(2, enumeratedContents.Count);
			Assert.IsTrue(enumeratedContents.Contains(new KeyValuePair<object, int>(keepAlive1, 0)));
			Assert.IsTrue(enumeratedContents.Contains(new KeyValuePair<object, int>(keepAlive2, 2)));
		}
	}
}

