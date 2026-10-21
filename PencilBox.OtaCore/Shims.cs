// <copyright file="Shims.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
// net48 编译垫片（编译期补齐 netstandard2.1+ 类型；仅本库可见，主线条目零改动）：
//   IsExternalInit（positional record 的 init 访问器）、Index/Range（字符串 ^.. 切片）+ 
//   RuntimeHelpers.GetSubString/GetSubArray（范围切片的编译器落点）。
namespace System.Runtime.CompilerServices
{
    using System;
    using System.Collections.Generic;

    internal static class IsExternalInit { }

    internal static class RuntimeHelpers
    {
        internal static string GetSubString(string text, Range range)
        {
            var (offset, length) = range.GetOffsetAndLength(text.Length);
            return text.Substring(offset, length);
        }

        internal static T[] GetSubArray<T>(T[] array, Range range)
        {
            var (offset, length) = range.GetOffsetAndLength(array.Length);
            var dest = new T[length];
            Array.Copy(array, offset, dest, 0, length);
            return dest;
        }
    }
}

namespace System
{
    internal readonly struct Index
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (fromEnd)
                value = ~value;
            _value = value;
        }

        public static Index FromStart(int value) => new(value);

        public static Index FromEnd(int value) => new(~value);

        internal int GetOffset(int length)
        {
            var v = _value;
            if (v < 0)
                v += length + 1;
            return v;
        }

        public static implicit operator Index(int value) => FromStart(value);
    }

    internal readonly struct Range
    {
        public Index Start { get; }

        public Index End { get; }

        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public static Range All => new(Index.FromStart(0), Index.FromEnd(0));

        public static Range StartAt(Index start) => new(start, Index.FromEnd(0));

        public static Range EndAt(Index end) => new(Index.FromStart(0), end);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new IndexOutOfRangeException();
            return (start, end - start);
        }
    }
}
