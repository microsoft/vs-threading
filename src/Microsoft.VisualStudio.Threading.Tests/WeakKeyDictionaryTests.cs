/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Xunit;
    using Xunit.Abstractions;

    /// <summary>
    /// Tests for the weak dictionary class
    /// </summary>
    public class WeakKeyDictionaryTests
    {
        /// <summary>
        /// Magic number size of strings to allocate for GC tests.
        /// </summary>
        private const int BigMemoryFootprintTest = 1 * 1024 * 1024;

        /// <summary>
        /// The xunit test logger.
        /// </summary>
        private readonly ITestOutputHelper logger;

        public WeakKeyDictionaryTests(ITestOutputHelper logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Find with the same key inserted using the indexer
        /// </summary>
        [Fact]
        public void Indexer_ReferenceFound()
        {
            string k1 = "key";
            string v1 = "value";

            var dictionary = new WeakKeyDictionary<string, string>();
            dictionary[k1] = v1;

            // Now look for the same key we inserted
            string v2 = dictionary[k1];

            Assert.True(object.ReferenceEquals(v1, v2));
            Assert.True(dictionary.ContainsKey(k1));
        }

        /// <summary>
        /// Find something not present with the indexer
        /// </summary>
        [Fact]
        public void Indexer_NotFound()
        {
            var dictionary = new WeakKeyDictionary<string, string>();
            Assert.Throws<KeyNotFoundException>(() => dictionary["x"]);
        }

        /// <summary>
        /// Find with the same key inserted using TryGetValue
        /// </summary>
        [Fact]
        public void TryGetValue_ReferenceFound()
        {
            string k1 = "key";
            string v1 = "value";

            var dictionary = new WeakKeyDictionary<string, string>();
            dictionary[k1] = v1;

            // Now look for the same key we inserted
            bool result = dictionary.TryGetValue(k1, out string v2);

            Assert.True(result);
            Assert.True(object.ReferenceEquals(v1, v2));
        }

        /// <summary>
        /// Find something not present with TryGetValue
        /// </summary>
        [Fact]
        public void TryGetValue_ReferenceNotFound()
        {
            var dictionary = new WeakKeyDictionary<string, string>();

            bool result = dictionary.TryGetValue("x", out string v);

            Assert.False(result);
            Assert.Null(v);
            Assert.False(dictionary.ContainsKey("x"));
        }

        /// <summary>
        /// Find a key that wasn't inserted but is equal
        /// </summary>
        [Fact]
        public void EqualityComparer()
        {
            string k1 = "key";
            string v1 = "value";

            var dictionary = new WeakKeyDictionary<string, string>();
            dictionary[k1] = v1;

            // Now look for a different but equatable key
            // Don't create it with a literal or the compiler will intern it!
            string k2 = string.Concat("k", "ey");

            Assert.False(object.ReferenceEquals(k1, k2));

            string v2 = dictionary[k2];

            Assert.True(object.ReferenceEquals(v1, v2));
        }

        /// <summary>
        /// Verify dictionary doesn't hold onto keys
        /// </summary>
        [Fact]
        public void KeysCollectable()
        {
            string v1 = new string('v', BigMemoryFootprintTest);
            WeakKeyDictionary<string, string> dictionary = AllocateDictionaryWithLargeKey(v1, out long memory1);

            long memory2 = GC.GetTotalMemory(true);

            // Key collected, should be about 2MB less
            long difference = memory1 - memory2;

            this.logger.WriteLine("Start {0}, end {1}, diff {2}", memory1, memory2, difference);
            Assert.True(difference > 1500000, $"Actual difference is {difference}."); // 2MB minus big noise allowance

            // This line is VERY important, as it keeps the GC from being too smart and collecting
            // the dictionary and its large strings because we never use them again.
            GC.KeepAlive(dictionary);
        }

        /// <summary>
        /// Call Scavenge explicitly
        /// </summary>
        [Fact]
        public void ExplicitScavenge()
        {
            var dictionary = new WeakKeyDictionary<object, object>();
            ExplicitScavenge_Helper(dictionary);

            GC.Collect();
            dictionary.Scavenge();

            Assert.Equal(0, dictionary.Count);
        }

        /// <summary>
        /// Growing should invoke Scavenge
        /// </summary>
        [Fact]
        public void ScavengeOnGrow()
        {
            var dictionary = new WeakKeyDictionary<object, object>();

            for (int i = 0; i < 100; i++)
            {
                dictionary[new object()] = new object();

                // Randomly collect some
                if (i == 15)
                {
                    GC.Collect();
                }
            }

            // We should have scavenged at least once
            this.logger.WriteLine("Count {0}", dictionary.Count);
            Assert.True(dictionary.Count < 100);

            // Finish with explicit scavenge
            int count1 = dictionary.Count;
            int removed = dictionary.Scavenge();
            int count2 = dictionary.Count;

            this.logger.WriteLine("Removed {0}", removed);
            Assert.Equal(removed, count1 - count2);
        }

        /// <summary>
        /// Tests that the enumerator correctly lists contents, skipping over collected elements.
        /// </summary>
        [Fact]
        public void Enumerator()
        {
            var dictionary = new WeakKeyDictionary<object, int>();
            object keepAlive1 = new object();
            object keepAlive2 = new object();
            dictionary[keepAlive1] = 0;
            Enumerator_Helper(dictionary);
            dictionary[keepAlive2] = 2;
            GC.Collect();

            var enumeratedContents = dictionary.ToList();
            Assert.Equal(2, enumeratedContents.Count);
            Assert.True(enumeratedContents.Contains(new KeyValuePair<object, int>(keepAlive1, 0)));
            Assert.True(enumeratedContents.Contains(new KeyValuePair<object, int>(keepAlive2, 2)));
        }

        /// <summary>
        /// A helper method to allocate a dictionary with a large key.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // must not be inlined so that locals are guaranteed to be freed.
        private static WeakKeyDictionary<string, string> AllocateDictionaryWithLargeKey(string v1, out long memoryAfterKeyAndBeforeDictionary)
        {
            string k1 = new string('k', BigMemoryFootprintTest);

            // Each character is 2 bytes, so about 4MB of this should be the strings
            memoryAfterKeyAndBeforeDictionary = GC.GetTotalMemory(true);
            return new WeakKeyDictionary<string, string>
            {
                [k1] = v1,
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // must not be inlined so that locals are guaranteed to be freed.
        private static void ExplicitScavenge_Helper(WeakKeyDictionary<object, object> dictionary)
        {
            object k1 = new object();
            object v1 = new object();

            dictionary[k1] = v1;

            Assert.Equal(1, dictionary.Count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // must not be inlined so that locals are guaranteed to be freed.
        private static void Enumerator_Helper(WeakKeyDictionary<object, int> dictionary)
        {
            object collected = new object();
            dictionary[collected] = 1;
        }
    }
}
