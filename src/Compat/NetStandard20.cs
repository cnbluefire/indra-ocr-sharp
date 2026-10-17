using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
#if !(NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER)
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
#endif

// The netstandard2.0 half of the three libraries (docs/335-multitarget-netstandard20.md).
//
// The port is written in modern C# against modern BCL types.  Everything it uses that arrived
// after netstandard2.0 is gathered here, and nothing in this file is public: it is compiled into
// each of the three libraries as a linked <Compile> item (see their csproj files), so the ns2.0
// assembly is self-contained and the net10.0 assembly never sees any of it.
//
// Two kinds of compatibility live here.
//
//   * PolySharp (a source generator, referenced on the ns2.0 target only) covers the
//     language-level polyfills -- `init` accessors, `record`, `Index`/`Range`, collection
//     expressions, `[CallerArgumentExpression]`, `[NotNull]`, `[DoesNotReturnIf]`, ... -- so
//     this file only has to carry what a source generator cannot add to an *existing* BCL type:
//     a missing type (System.MathF, which can simply be declared), members whose body a `#if`
//     dispatches to the framework's own implementation (Guard, Bits, MathCompat, ...), and
//     members re-declared as extension methods so that not one call site has to change.
//
//   * Every `#if` names the *real* minimum version of the member it guards -- NET6_0_OR_GREATER
//     for ArgumentNullException.ThrowIfNull, NET7_0_OR_GREATER for ObjectDisposedException.ThrowIf,
//     NET8_0_OR_GREATER for List<T>.AddRange(ReadOnlySpan<T>), and
//     `NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER` for the members that arrived with
//     .NET Standard 2.1 / .NET Core 2.1.  A target that has the member always takes the
//     framework's own path -- which is what keeps the net10.0 build bit-for-bit what it was --
//     and only a target below the version gets the shim.  (The `||` is needed because .NET 5 and
//     later define the NETCOREAPP*_OR_GREATER symbols and never NETSTANDARD2_1_OR_GREATER.)
//
// Numeric parity is the one thing the ns2.0 target cannot have, and it is worth being explicit
// about it here: .NET Framework has no CRT `expf` behind `MathF.Exp` and no fused multiply-add at
// all, and both are load-bearing for the numpy transcriptions (Numerics/NpyExp.cs reproduces
// numpy's own exp kernel, `logaddexp`'s float32 path calls the CRT).  On ns2.0 those two come out
// up to 1 ulp away, which can flip a detector threshold.  The ns2.0 build is therefore a
// *functional* .NET Framework/NuGet target, not a bit-exact one; docs/335-multitarget-netstandard20.md
// says so, and the net10.0 build remains the reference.

namespace Indra.Ocr
{
    /// <summary>
    /// The argument guards the port calls as <c>XException.ThrowIf*</c>.
    /// </summary>
    internal static class Guard
    {
        /// <summary><c>ArgumentNullException.ThrowIfNull</c> (.NET 6).</summary>
        public static void NotNull(
            [NotNull] object? value,
            [CallerArgumentExpression(nameof(value))] string? name = null)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(value, name);
#else
            if (value is null)
            {
                throw new ArgumentNullException(name);
            }
#endif
        }

        /// <summary>
        /// <c>ArgumentException.ThrowIfNullOrEmpty</c> (.NET 6): null names the argument, empty
        /// does not.
        /// </summary>
        public static void NotNullOrEmpty(
            [NotNull] string? value,
            [CallerArgumentExpression(nameof(value))] string? name = null)
        {
#if NET6_0_OR_GREATER
            ArgumentException.ThrowIfNullOrEmpty(value, name);
#else
            if (value is null)
            {
                throw new ArgumentNullException(name);
            }

            if (value.Length == 0)
            {
                // The framework's own wording for it (SR.Argument_EmptyString).
                throw new ArgumentException("The value cannot be an empty string.", name);
            }
#endif
        }

        /// <summary><c>ObjectDisposedException.ThrowIf</c> (.NET 7).</summary>
        public static void NotDisposed([DoesNotReturnIf(true)] bool condition, object instance)
        {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(condition, instance);
#else
            if (condition)
            {
                // What the framework passes as the object name.
                throw new ObjectDisposedException(instance.GetType().FullName);
            }
#endif
        }
    }

    /// <summary>
    /// <c>BitConverter</c>'s single-precision bit reinterpretations (netstandard2.1), which the
    /// fixed-point kernels use on every element, so the older target goes through
    /// <see cref="Unsafe"/> rather than a byte array.
    /// </summary>
    internal static class Bits
    {
        public static float Int32BitsToSingle(int value)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            return BitConverter.Int32BitsToSingle(value);
#else
            return Unsafe.As<int, float>(ref value);
#endif
        }

        public static int SingleToInt32Bits(float value)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            return BitConverter.SingleToInt32Bits(value);
#else
            return Unsafe.As<float, int>(ref value);
#endif
        }
    }

    /// <summary>
    /// <c>Math.Clamp</c> (netstandard2.1).  The older target keeps the framework's semantics,
    /// including the way it passes NaN straight through.
    /// </summary>
    internal static class MathCompat
    {
        public static int Clamp(int value, int min, int max)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            return Math.Clamp(value, min, max);
#else
            return value < min ? min : value > max ? max : value;
#endif
        }

        public static float Clamp(float value, float min, float max)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            return Math.Clamp(value, min, max);
#else
            return value < min ? min : value > max ? max : value;
#endif
        }

        public static double Clamp(double value, double min, double max)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            return Math.Clamp(value, min, max);
#else
            return value < min ? min : value > max ? max : value;
#endif
        }
    }

    /// <summary>
    /// <c>CollectionsMarshal.AsSpan</c> (.NET 5), which the port uses read-only to hand a
    /// <see cref="List{T}"/> to the span-taking numeric helpers.  Below .NET 5 the BCL exposes no
    /// such alias, so the shim takes the same shortcut the BCL's own <c>AsSpan</c> takes: it reads
    /// the list's own storage.  The call sites -- and the contract that the list must not be added
    /// to while the span is live -- are the same on every target.
    /// </summary>
    internal static class ListCompat
    {
        public static ReadOnlySpan<T> AsSpan<T>(List<T> list)
        {
#if NET5_0_OR_GREATER
            return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
#else
            return list is null ? default : new ReadOnlySpan<T>(Items(list), 0, list.Count);
#endif
        }

#if !NET5_0_OR_GREATER
        /// <summary>
        /// A list's elements are one contiguous array, and that array is the list's *first* field
        /// (<c>List&lt;T&gt;._items</c> on .NET Framework, on Mono and on .NET).  Its own source says
        /// why it stays there: <c>// Do not rename (binary serialization) [cDAC] [ComWrappers] :
        /// Contract depends on this exact name</c> — the name is in every serialized list, and the
        /// debugger's cDAC contracts and the ComWrappers data contract resolve it by name.  C#
        /// cannot name the field (the indexer returns by value, so there is no <c>ref list[0]</c>),
        /// so the list is read as a class whose first field is that array:
        /// <see cref="ListLayout{T}.Items"/> then reads the object's own first field.
        /// (<c>MemoryMarshal.CreateSpan</c> would turn a ref into a span, but System.Memory's
        /// netstandard2.0 asset does not have it; the array constructor does the same job.)
        /// </summary>
        private static T[] Items<T>(List<T> list) => Unsafe.As<List<T>, ListLayout<T>>(ref list).Items;

        /// <summary>
        /// The layout of the only <see cref="List{T}"/> field this file reads.  Nothing may be
        /// declared before it.
        /// </summary>
        private sealed class ListLayout<T>
        {
            public T[] Items = null!;
        }
#endif
    }

    /// <summary>
    /// <c>CharUnicodeInfo.GetUnicodeCategory(int)</c> (netstandard2.1), which the port uses to
    /// classify a code point exactly as <c>unicodedata</c> does (Text/UnicodeText.cs).  Below
    /// that there is only the <c>char</c> overload, so an astral code point is looked up through
    /// the string overload, which resolves the surrogate pair the same way the int one does.
    /// </summary>
    internal static class UnicodeCompat
    {
        public static System.Globalization.UnicodeCategory Category(int codePoint)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            return System.Globalization.CharUnicodeInfo.GetUnicodeCategory(codePoint);
#else
            return codePoint > char.MaxValue
                ? System.Globalization.CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0)
                : System.Globalization.CharUnicodeInfo.GetUnicodeCategory((char)codePoint);
#endif
        }
    }

    /// <summary>
    /// The members the port calls as *instance* members of BCL types that a target can be
    /// missing, re-declared as extension methods.  A call site reads exactly the same on every
    /// target: wherever the framework has the member, its own instance member is found first and
    /// the extension is not compiled at all.
    /// </summary>
    internal static class CompatExtensions
    {
#if !(NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER)
        /// <summary><c>string.Contains(char)</c> (netstandard2.1).</summary>
        public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;

        /// <summary><c>string.StartsWith(char)</c> (netstandard2.1).</summary>
        public static bool StartsWith(this string text, char value) =>
            text.Length > 0 && text[0] == value;

        /// <summary><c>string.EndsWith(char)</c> (netstandard2.1).</summary>
        public static bool EndsWith(this string text, char value) =>
            text.Length > 0 && text[text.Length - 1] == value;

        /// <summary><c>string.Split(char, StringSplitOptions)</c> (netstandard2.1).</summary>
        public static string[] Split(this string text, char separator, StringSplitOptions options) =>
            text.Split(new[] { separator }, options);

        /// <summary>
        /// <c>string.Replace(string, string, StringComparison)</c> (netstandard2.1).
        /// </summary>
        public static string Replace(this string text, string oldValue, string newValue, StringComparison comparison)
        {
            if (oldValue is null)
            {
                throw new ArgumentNullException(nameof(oldValue));
            }

            if (oldValue.Length == 0)
            {
                throw new ArgumentException("The value cannot be an empty string.", nameof(oldValue));
            }

            if (newValue is null)
            {
                throw new ArgumentNullException(nameof(newValue));
            }

            int at = text.IndexOf(oldValue, comparison);
            if (at < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            int done = 0;
            while (at >= 0)
            {
                builder.Append(text, done, at - done).Append(newValue);
                done = at + oldValue.Length;
                at = text.IndexOf(oldValue, done, comparison);
            }

            return builder.Append(text, done, text.Length - done).ToString();
        }

        /// <summary><c>Dictionary.TryAdd</c> (netstandard2.1).</summary>
        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
            where TKey : notnull
        {
            if (dictionary.ContainsKey(key))
            {
                return false;
            }

            dictionary.Add(key, value);
            return true;
        }

        /// <summary><c>Enumerable.ToHashSet</c> (netstandard2.1).</summary>
        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source) => new HashSet<T>(source);

        /// <summary>
        /// <c>Stream.ReadAsync(Memory&lt;byte&gt;, CancellationToken)</c> (netstandard2.1).  There
        /// is no way to read into a <see cref="Memory{T}"/> before it, so the bytes come back
        /// through a pooled buffer; the port only uses this for the model download's progress,
        /// where the extra copy is not on any measured path.
        /// </summary>
        public static Task<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            return ReadAsyncCore(stream, buffer, rented, cancellationToken);
        }

        /// <summary><c>Stream.WriteAsync(ReadOnlyMemory&lt;byte&gt;, CancellationToken)</c> (netstandard2.1).</summary>
        public static Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            buffer.Span.CopyTo(rented);
            return WriteAsyncCore(stream, rented, buffer.Length, cancellationToken);
        }

        private static async Task<int> ReadAsyncCore(
            Stream stream, Memory<byte> buffer, byte[] rented, CancellationToken cancellationToken)
        {
            try
            {
                int read = await stream.ReadAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                rented.AsSpan(0, read).CopyTo(buffer.Span);
                return read;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static async Task WriteAsyncCore(
            Stream stream, byte[] rented, int count, CancellationToken cancellationToken)
        {
            try
            {
                await stream.WriteAsync(rented, 0, count, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
#endif

#if !NET8_0_OR_GREATER
        /// <summary><c>List&lt;T&gt;.AddRange(ReadOnlySpan&lt;T&gt;)</c> (.NET 8).</summary>
        public static void AddRange<T>(this List<T> list, ReadOnlySpan<T> values)
        {
            foreach (T value in values)
            {
                list.Add(value);
            }
        }
#endif
    }
}

// System.MathF arrived in netstandard2.1, so the targets below it get the polyfill.  Internal, so
// it stays inside whichever assembly compiled it and cannot collide with a consumer's own copy.
#if !(NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER)
namespace System
{
    /// <summary>
    /// <c>System.MathF</c> (netstandard2.1), declared here because a target below it has no such
    /// type at all.
    /// </summary>
    /// <remarks>
    /// Every member is either exact for single precision (<c>Abs</c>, <c>Min</c>, <c>Max</c>,
    /// <c>Floor</c>, <c>Ceiling</c>, <c>Round</c>) or comes from rounding the double
    /// implementation (<c>Exp</c>).  The latter is where parity is lost: .NET Framework has no
    /// CRT <c>expf</c> for .NET's <c>MathF.Exp</c> to forward to, so it can land 1 ulp away from
    /// what numpy/the reference computes (docs/335-multitarget-netstandard20.md).
    /// <para>
    /// <c>FusedMultiplyAdd</c> is the bigger one: no .NET Framework JIT emits a fused
    /// multiply-add, so this body is the unfused <c>x * y + z</c> -- the very rounding the numpy
    /// transcriptions in Numerics/ exist to avoid.  It is still the best available on that
    /// target, and the net10.0 build keeps the real fused instruction.
    /// </para>
    /// </remarks>
    internal static class MathF
    {
        public static float Abs(float x) => Math.Abs(x);

        public static float Ceiling(float x) => (float)Math.Ceiling(x);

        public static float Exp(float x) => (float)Math.Exp(x);

        public static float Floor(float x) => (float)Math.Floor(x);

        public static float FusedMultiplyAdd(float x, float y, float z) => x * y + z;

        public static float Max(float x, float y) => Math.Max(x, y);

        public static float Min(float x, float y) => Math.Min(x, y);

        public static float Round(float x) => (float)Math.Round(x);

        public static float Round(float x, MidpointRounding mode) => (float)Math.Round(x, mode);
    }
}
#endif
