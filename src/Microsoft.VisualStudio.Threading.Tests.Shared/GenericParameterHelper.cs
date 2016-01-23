/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Tests.Shared
{
    using System;

    public class GenericParameterHelper
    {
        public GenericParameterHelper()
        {
            this.Data = new Random().Next();
        }

        public GenericParameterHelper(int data)
        {
            this.Data = data;
        }

        public int Data { get; set; }

        public override bool Equals(object obj)
        {
            var other = obj as GenericParameterHelper;
            if (other != null)
            {
                return this.Data == other.Data;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return this.Data;
        }
    }
}
