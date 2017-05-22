﻿using Impatient.Query.Expressions;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Impatient.Query.ExpressionVisitors.Optimizing
{
    public class MemberAccessReducingExpressionVisitor : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            var expression = Visit(node.Expression);

            switch (expression)
            {
                case MemberInitExpression memberInitExpression
                when FindExpressionForMember(memberInitExpression, node.Member, out var foundExpression):
                {
                    return Visit(foundExpression);
                }

                case NewExpression newExpression
                when FindExpressionForMember(newExpression, node.Member, out var foundExpression):
                {
                    return Visit(foundExpression);
                }

                // TODO: Remove this to make this visitor more generalized.
                case RelationalGroupingExpression relationalGroupingExpression
                when node.Member == relationalGroupingExpression.Type.GetRuntimeProperty(nameof(IGrouping<object, object>.Key)):
                {
                    return Visit(relationalGroupingExpression.KeySelector);
                }

                default:
                {
                    return node.Update(expression);
                }
            }
        }

        private static bool FindExpressionForMember(MemberInitExpression memberInitExpression, MemberInfo memberInfo, out Expression expression)
        {
            var memberBinding
                = memberInitExpression.Bindings
                    .OfType<MemberAssignment>()
                    .SingleOrDefault(b => b.Member == memberInfo);

            if (memberBinding != null)
            {
                expression = memberBinding.Expression;

                return true;
            }

            return FindExpressionForMember(memberInitExpression.NewExpression, memberInfo, out expression);
        }

        private static bool FindExpressionForMember(NewExpression newExpression, MemberInfo memberInfo, out Expression expression)
        {
            if (newExpression.Members != null)
            {
                var memberIndex = newExpression.Members.IndexOf(memberInfo);

                if (memberIndex != -1)
                {
                    expression = newExpression.Arguments[memberIndex];

                    return true;
                }
            }

            expression = null;

            return false;
        }
    }
}