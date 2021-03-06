﻿using Impatient.Query.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Impatient.Query.Expressions
{
    public class ExtraPropertyAccessExpression : Expression, ISemanticHashCodeProvider
    {
        public ExtraPropertyAccessExpression(Expression expression, string property, Type type)
        {
            Expression = expression;
            Property = property;
            Type = type;
        }

        protected override Expression VisitChildren(ExpressionVisitor visitor)
        {
            var expression = visitor.Visit(Expression);

            if (expression != Expression)
            {
                return new ExtraPropertyAccessExpression(expression, Property, Type);
            }

            return this;
        }

        public override ExpressionType NodeType => ExpressionType.Extension;

        public override Type Type { get; }

        public override bool CanReduce => false;

        public Expression Expression { get; }

        public new string Property { get; }

        public void Deconstruct(out Expression expression, out string property)
        {
            expression = Expression;
            property = Property;
        }

        public int GetSemanticHashCode(ExpressionEqualityComparer comparer)
        {
            unchecked
            {
                var hash = comparer.GetHashCode(Expression);

                hash = (hash * 16777619) ^ Property.GetHashCode();

                return hash;
            }
        }
    }
}
