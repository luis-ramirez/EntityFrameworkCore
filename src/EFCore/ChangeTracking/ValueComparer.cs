// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Utilities;
using Remotion.Linq.Parsing.ExpressionVisitors;

namespace Microsoft.EntityFrameworkCore.ChangeTracking
{
    /// <summary>
    ///     <para>
    ///         Specifies custom value snapshotting and comparison for
    ///         CLR types that cannot be compared with <see cref="object.Equals(object, object)" />
    ///         and/or need a deep copy when taking a snapshot. For example, arrays of primitive types
    ///         will require both if mutation is to be detected.
    ///     </para>
    ///     <para>
    ///         Snapshotting is the process of creating a copy of the value into a snapshot so it can
    ///         later be compared to determine if it has changed. For some types, such as collections,
    ///         this needs to be a deep copy of the collection rather than just a shallow copy of the
    ///         reference.
    ///     </para>
    /// </summary>
    public abstract class ValueComparer
    {
        internal static readonly MethodInfo EqualityComparerHashCodeMethod
            = typeof(IEqualityComparer).GetTypeInfo()
                .GetDeclaredMethod(nameof(IEqualityComparer.GetHashCode));

        internal static readonly MethodInfo EqualityComparerEqualsMethod
            = typeof(IEqualityComparer).GetTypeInfo()
                .GetDeclaredMethod(nameof(IEqualityComparer.Equals));

        internal static readonly MethodInfo ObjectEqualsMethod = typeof(object).GetTypeInfo().DeclaredMethods.Single(
            m => m.IsStatic
                 && m.ReturnType == typeof(bool)
                 && nameof(object.Equals).Equals(m.Name, StringComparison.Ordinal)
                 && m.IsPublic
                 && m.GetParameters().Length == 2
                 && m.GetParameters()[0].ParameterType == typeof(object)
                 && m.GetParameters()[1].ParameterType == typeof(object));

        internal static readonly MethodInfo ObjectGetHashCodeMethod = typeof(object).GetTypeInfo().DeclaredMethods.Single(
            m => m.ReturnType == typeof(int)
                 && nameof(GetHashCode).Equals(m.Name, StringComparison.Ordinal)
                 && m.IsPublic
                 && m.GetParameters().Length == 0);

        /// <summary>
        ///     Creates a new <see cref="ValueComparer" /> with the given comparison and
        ///     snapshotting expressions.
        /// </summary>
        /// <param name="equalsExpression"> The comparison expression. </param>
        /// <param name="hashCodeExpression"> The associated hash code generator. </param>
        /// <param name="keyEqualsExpression"> The comparison expression to use when comparing key values. </param>
        /// <param name="keyHashCodeExpression"> The associated hash code generator to use for key values. </param>
        /// <param name="snapshotExpression"> The snapshot expression. </param>
        protected ValueComparer(
            [NotNull] LambdaExpression equalsExpression,
            [NotNull] LambdaExpression hashCodeExpression,
            [NotNull] LambdaExpression keyEqualsExpression,
            [NotNull] LambdaExpression keyHashCodeExpression,
            [NotNull] LambdaExpression snapshotExpression)
        {
            Check.NotNull(equalsExpression, nameof(equalsExpression));
            Check.NotNull(hashCodeExpression, nameof(hashCodeExpression));
            Check.NotNull(keyEqualsExpression, nameof(keyEqualsExpression));
            Check.NotNull(keyHashCodeExpression, nameof(keyHashCodeExpression));
            Check.NotNull(snapshotExpression, nameof(snapshotExpression));

            EqualsExpression = equalsExpression;
            HashCodeExpression = hashCodeExpression;
            KeyEqualsExpression = keyEqualsExpression;
            KeyHashCodeExpression = keyHashCodeExpression;
            SnapshotExpression = snapshotExpression;
        }

        /// <summary>
        ///     The type.
        /// </summary>
        public abstract Type Type { get; }

        /// <summary>
        ///     The comparison expression compiled into an untyped delegate.
        /// </summary>
        public new abstract Func<object, object, bool> Equals { get; }

        /// <summary>
        ///     The hash code expression compiled into an untyped delegate.
        /// </summary>
        public abstract Func<object, int> HashCode { get; }

        /// <summary>
        ///     The key comparison expression compiled into an untyped delegate.
        /// </summary>
        public abstract Func<object, object, bool> KeyEquals { get; }

        /// <summary>
        ///     The key hash code expression compiled into an untyped delegate.
        /// </summary>
        public abstract Func<object, int> KeyHashCode { get; }

        /// <summary>
        ///     <para>
        ///         The snapshot expression compiled into an untyped delegate.
        ///     </para>
        ///     <para>
        ///         Snapshotting is the process of creating a copy of the value into a snapshot so it can
        ///         later be compared to determine if it has changed. For some types, such as collections,
        ///         this needs to be a deep copy of the collection rather than just a shallow copy of the
        ///         reference.
        ///     </para>
        /// </summary>
        public abstract Func<object, object> Snapshot { get; }

        /// <summary>
        ///     The comparison expression.
        /// </summary>
        public virtual LambdaExpression EqualsExpression { get; }

        /// <summary>
        ///     The hash code expression.
        /// </summary>
        public virtual LambdaExpression HashCodeExpression { get; }

        /// <summary>
        ///     The comparison expression to use when comparing key values.
        /// </summary>
        public virtual LambdaExpression KeyEqualsExpression { get; }

        /// <summary>
        ///     The hash code expression to use when comparing key values.
        /// </summary>
        public virtual LambdaExpression KeyHashCodeExpression { get; }

        /// <summary>
        ///     <para>
        ///         The snapshot expression.
        ///     </para>
        ///     <para>
        ///         Snapshotting is the process of creating a copy of the value into a snapshot so it can
        ///         later be compared to determine if it has changed. For some types, such as collections,
        ///         this needs to be a deep copy of the collection rather than just a shallow copy of the
        ///         reference.
        ///     </para>
        /// </summary>
        public virtual LambdaExpression SnapshotExpression { get; }

        /// <summary>
        ///     Transforms a given lambda expression, which must be of the form <c>&lt;T, T, bool&gt;</c>,
        ///     to accept <c>&lt;T?, T?, bool&gt;</c>, but without any handling for possible null values.
        /// </summary>
        /// <param name="equalsExpression"> The original expression. </param>
        /// <returns> The transformed expression. </returns>
        public static LambdaExpression TransformEqualsForNonNullNullable(
            [NotNull] LambdaExpression equalsExpression)
        {
            Check.NotNull(equalsExpression, nameof(equalsExpression));

            var type = equalsExpression.Parameters[0].Type;
            var nullableType = type.MakeNullable();

            var newParam1 = Expression.Parameter(nullableType, "v1");
            var newParam2 = Expression.Parameter(nullableType, "v2");

            return Expression.Lambda(
                ReplaceEqualsParameters(
                    equalsExpression,
                    Expression.Convert(newParam1, type),
                    Expression.Convert(newParam2, type)),
                newParam1, newParam2);
        }

        /// <summary>
        ///     Takes the given lambda expression, which must be of the form <c>&lt;T, T, bool&gt;</c>,
        ///     and replaces the two parameters with the given expressions, returning the new body.
        /// </summary>
        /// <param name="equalsExpression"> The equality expression. </param>
        /// <param name="leftExpression"> The new left expression. </param>
        /// <param name="rightExpression"> The new right expression. </param>
        /// <returns> The body of the lambda with left and right parameters replaced.</returns>
        public static Expression ReplaceEqualsParameters(
            [NotNull] LambdaExpression equalsExpression,
            [NotNull] Expression leftExpression,
            [NotNull] Expression rightExpression)
        {
            Check.NotNull(equalsExpression, nameof(equalsExpression));
            Check.NotNull(leftExpression, nameof(leftExpression));
            Check.NotNull(rightExpression, nameof(rightExpression));

            return ReplacingExpressionVisitor.Replace(
                equalsExpression.Parameters[1],
                rightExpression,
                ReplacingExpressionVisitor.Replace(
                    equalsExpression.Parameters[0],
                    leftExpression,
                    equalsExpression.Body));
        }

        /// <summary>
        ///     Transforms a given lambda expression, which must be of the form <c>&lt;T, int&gt;</c>,
        ///     to accept <c>&lt;T?, int&gt;</c>, but without any handling for possible null values.
        /// </summary>
        /// <param name="hashCodeExpression"> The original expression. </param>
        /// <returns> The transformed expression. </returns>
        public static LambdaExpression TransformHashCodeForNonNullNullable(
            [NotNull] LambdaExpression hashCodeExpression)
        {
            Check.NotNull(hashCodeExpression, nameof(hashCodeExpression));

            var type = hashCodeExpression.Parameters[0].Type;
            var newParam = Expression.Parameter(type.MakeNullable(), "v");

            return Expression.Lambda(
                ReplaceHashCodeParameter(
                    hashCodeExpression,
                    Expression.Convert(newParam, type)),
                newParam);
        }

        /// <summary>
        ///     Takes the given lambda expression, which must be of the form <c>&lt;T, int&gt;</c>,
        ///     and replaces the parameters with the given expression, returning the new body.
        /// </summary>
        /// <param name="hashCodeExpression"> The hash code expression. </param>
        /// <param name="expression"> The new expression. </param>
        /// <returns> The body of the lambda with the parameter replaced.</returns>
        public static Expression ReplaceHashCodeParameter(
            [NotNull] LambdaExpression hashCodeExpression,
            [NotNull] Expression expression)
        {
            Check.NotNull(hashCodeExpression, nameof(hashCodeExpression));
            Check.NotNull(expression, nameof(expression));

            return ReplacingExpressionVisitor.Replace(
                hashCodeExpression.Parameters[0],
                expression,
                hashCodeExpression.Body);
        }
    }
}
