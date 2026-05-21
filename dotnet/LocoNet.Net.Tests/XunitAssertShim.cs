using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MSAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace LocoNet.Net.Tests;

/// <summary>
/// xUnit-style <c>Assert</c> facade backed by MSTest. Lives in the same namespace as the
/// test classes so it shadows <c>Microsoft.VisualStudio.TestTools.UnitTesting.Assert</c>
/// without each test file needing extra usings.
/// </summary>
internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        // xUnit's Equal does structural/sequence equality for collections; mimic that
        // so byte[] / IEnumerable comparisons don't fall back to reference equality.
        if (expected is IEnumerable ee && actual is IEnumerable ae && expected is not string)
        {
            var el = ee.Cast<object?>().ToList();
            var al = ae.Cast<object?>().ToList();
            MSAssert.AreEqual(el.Count, al.Count, "Collection length mismatch.");
            for (int i = 0; i < el.Count; i++)
            {
                MSAssert.AreEqual(el[i], al[i], $"Mismatch at index {i}.");
            }
            return;
        }
        MSAssert.AreEqual(expected, actual);
    }
    public static void NotEqual<T>(T expected, T actual) => MSAssert.AreNotEqual(expected, actual);

    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (expected is null && actual is null) return;
        MSAssert.IsNotNull(expected);
        MSAssert.IsNotNull(actual);
        var e = expected.ToList();
        var a = actual.ToList();
        MSAssert.AreEqual(e.Count, a.Count, "Collection length mismatch.");
        for (int i = 0; i < e.Count; i++)
        {
            MSAssert.AreEqual(e[i], a[i], $"Mismatch at index {i}.");
        }
    }

    public static void True(bool? condition) => MSAssert.IsTrue(condition == true);
    public static void True(bool condition) => MSAssert.IsTrue(condition);
    public static void True(bool condition, string userMessage) => MSAssert.IsTrue(condition, userMessage);
    public static void True(bool? condition, string userMessage) => MSAssert.IsTrue(condition == true, userMessage);

    public static void False(bool? condition) => MSAssert.IsFalse(condition == true);
    public static void False(bool condition) => MSAssert.IsFalse(condition);
    public static void False(bool condition, string userMessage) => MSAssert.IsFalse(condition, userMessage);
    public static void False(bool? condition, string userMessage) => MSAssert.IsFalse(condition == true, userMessage);

    public static void StartsWith(string expectedStartString, string? actualString)
    {
        MSAssert.IsNotNull(actualString);
        MSAssert.IsTrue(actualString!.StartsWith(expectedStartString, StringComparison.Ordinal),
            $"Expected string to start with '{expectedStartString}' but got '{actualString}'.");
    }

    public static void Collection<T>(IEnumerable<T> collection, params Action<T>[] elementInspectors)
    {
        var list = collection.ToList();
        MSAssert.AreEqual(elementInspectors.Length, list.Count, "Collection length mismatch.");
        for (int i = 0; i < list.Count; i++)
        {
            elementInspectors[i](list[i]);
        }
    }

    public static void Null(object? value) => MSAssert.IsNull(value);
    public static T NotNull<T>(T? value) where T : class
    {
        MSAssert.IsNotNull(value);
        return value!;
    }
    public static T NotNull<T>(T? value) where T : struct
    {
        MSAssert.IsNotNull(value);
        return value!.Value;
    }

    public static void Same(object? expected, object? actual) => MSAssert.AreSame(expected, actual);
    public static void NotSame(object? expected, object? actual) => MSAssert.AreNotSame(expected, actual);

    public static void Empty(IEnumerable collection)
    {
        foreach (var _ in collection)
        {
            MSAssert.Fail("Expected empty collection.");
        }
    }

    public static void NotEmpty(IEnumerable collection)
    {
        foreach (var _ in collection) return;
        MSAssert.Fail("Expected non-empty collection.");
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        var list = collection as IList<T> ?? collection.ToList();
        MSAssert.AreEqual(1, list.Count, "Expected single element.");
        return list[0];
    }

    public static void Contains<T>(IEnumerable<T> collection, Func<T, bool> predicate)
    {
        foreach (var item in collection)
        {
            if (predicate(item)) return;
        }
        MSAssert.Fail("Expected at least one matching element.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Func<T, bool> predicate)
    {
        foreach (var item in collection)
        {
            if (predicate(item)) MSAssert.Fail("Expected no matching element.");
        }
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex)
        {
            MSAssert.Fail($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        MSAssert.Fail($"Expected {typeof(T).Name} but no exception was thrown.");
        throw new InvalidOperationException("unreachable");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T ex) { return ex; }
        catch (Exception ex)
        {
            MSAssert.Fail($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        MSAssert.Fail($"Expected {typeof(T).Name} but no exception was thrown.");
        throw new InvalidOperationException("unreachable");
    }

    public static async Task<Exception> ThrowsAnyAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { return ex; }
        MSAssert.Fail("Expected an exception but none was thrown.");
        throw new InvalidOperationException("unreachable");
    }
}
