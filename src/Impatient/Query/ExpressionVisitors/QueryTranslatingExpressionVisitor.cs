﻿using Impatient.Query.Expressions;
using Impatient.Query.ExpressionVisitors;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Impatient.Query.ExpressionVisitors
{
    public class QueryTranslatingExpressionVisitor : ExpressionVisitor
    {
        private int queryDepth = -1;
        private HashSet<string> tableAliases = new HashSet<string>();
        private IDictionary<AliasedTableExpression, string> aliasLookup = new Dictionary<AliasedTableExpression, string>();

        private DbCommandBuilderExpressionBuilder builder = new DbCommandBuilderExpressionBuilder();

        public (Expression materializer, Expression commandBuilder) Translate(SelectExpression selectExpression)
        {
            selectExpression = VisitAndConvert(selectExpression, nameof(Translate));

            return (selectExpression.Projection.Flatten(), builder.Build());
        }

        #region ExpressionVisitor overrides

        public override Expression Visit(Expression node)
        {
            if (IsParameterizable(node))
            {
                builder.AddParameter(node, FormatParameter);

                return node;
            }

            return base.Visit(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            switch (node.NodeType)
            {
                case ExpressionType.Coalesce:
                {
                    builder.Append("COALESCE(");
                    Visit(node.Left);
                    builder.Append(", ");
                    Visit(node.Right);
                    builder.Append(")");

                    return node;
                }
            }

            var left = node.Left;
            var right = node.Right;

            if (left is NewExpression leftNewExpression
                && right is NewExpression rightNewExpression)
            {
                return Visit(
                    leftNewExpression.Arguments
                        .Zip(rightNewExpression.Arguments, Expression.Equal)
                        .Aggregate(Expression.AndAlso));
            }

            switch (left)
            {
                case BinaryExpression leftBinaryExpression:
                {
                    builder.Append("(");

                    left = VisitBinary(leftBinaryExpression);

                    builder.Append(")");

                    break;
                }

                default:
                {
                    left = Visit(node.Left);

                    break;
                }
            }

            switch (node.NodeType)
            {
                case ExpressionType.AndAlso:
                {
                    builder.Append(" AND ");
                    break;
                }

                case ExpressionType.OrElse:
                {
                    builder.Append(" OR ");
                    break;
                }

                case ExpressionType.Equal:
                {
                    builder.Append(" = ");
                    break;
                }

                case ExpressionType.NotEqual:
                {
                    builder.Append(" <> ");
                    break;
                }

                case ExpressionType.GreaterThan:
                {
                    builder.Append(" > ");
                    break;
                }

                case ExpressionType.GreaterThanOrEqual:
                {
                    builder.Append(" >= ");
                    break;
                }

                case ExpressionType.LessThan:
                {
                    builder.Append(" < ");
                    break;
                }

                case ExpressionType.LessThanOrEqual:
                {
                    builder.Append(" <= ");
                    break;
                }

                case ExpressionType.Add:
                {
                    builder.Append(" + ");
                    break;
                }

                case ExpressionType.Subtract:
                {
                    builder.Append(" - ");
                    break;
                }

                case ExpressionType.Multiply:
                {
                    builder.Append(" * ");
                    break;
                }

                case ExpressionType.Divide:
                {
                    builder.Append(" / ");
                    break;
                }

                default:
                {
                    throw new NotSupportedException();
                }
            }

            switch (node.Right)
            {
                case BinaryExpression rightBinaryExpression:
                {
                    builder.Append("(");

                    right = VisitBinary(rightBinaryExpression);

                    builder.Append(")");

                    break;
                }

                default:
                {
                    right = Visit(node.Right);

                    break;
                }
            }

            return node.Update(left, node.Conversion, right);
        }

        protected override Expression VisitConditional(ConditionalExpression node)
        {
            builder.Append("(CASE WHEN ");

            var test = Visit(node.Test);

            builder.Append(" THEN ");

            var ifTrue = Visit(node.IfTrue);

            builder.Append(" ELSE ");

            var ifFalse = Visit(node.IfFalse);

            builder.Append(" END)");

            return node.Update(test, ifTrue, ifFalse);
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            switch (node.Value)
            {
                case string value:
                {
                    builder.Append($@"N'{value}'");
                    break;
                }

                case bool value:
                {
                    builder.Append(value ? "1" : "0");
                    break;
                }

                case object value:
                {
                    builder.Append(value.ToString());
                    break;
                }

                case null:
                {
                    builder.Append("NULL");
                    break;
                }
            }

            return node;
        }

        protected override Expression VisitExtension(Expression node)
        {
            switch (node)
            {
                case SelectExpression selectExpression:
                {
                    queryDepth++;

                    builder.Append("SELECT ");

                    if (selectExpression.IsDistinct)
                    {
                        builder.Append("DISTINCT ");
                    }

                    if (selectExpression.Limit != null && selectExpression.Offset == null)
                    {
                        builder.Append("TOP (");

                        selectExpression = selectExpression.UpdateLimit(Visit(selectExpression.Limit));

                        builder.Append(") ");
                    }

                    var readerParameter = Expression.Parameter(typeof(DbDataReader));

                    var projectionVisitor = new ReaderParameterInjectingExpressionVisitor(readerParameter);

                    var selectorBody = FlattenProjection(selectExpression.Projection, projectionVisitor);

                    var projectionExpressions
                        = projectionVisitor.GatheredExpressions
                            .Select((p, i) => (i, p.Key, p.Value))
                            .DefaultIfEmpty((0, null, Expression.Constant(1)));

                    foreach (var (index, alias, expression) in projectionExpressions)
                    {
                        if (index > 0)
                        {
                            builder.Append(", ");
                        }

                        if (queryDepth == 0
                            && expression.Type.IsBoolean()
                            && !(expression is SqlAliasExpression
                               || expression is SqlColumnExpression
                               || expression is SqlCastExpression))
                        {
                            builder.Append("CAST(");

                            EmitExpressionListExpression(expression);

                            builder.Append(" AS BIT)");
                        }
                        else
                        {
                            EmitExpressionListExpression(expression);
                        }

                        if (!string.IsNullOrEmpty(alias))
                        {
                            builder.Append(" AS ");
                            builder.Append(FormatIdentifier(alias));
                        }
                    }

                    if (selectExpression.Table != null)
                    {
                        builder.AppendLine();
                        builder.Append($"FROM ");

                        Visit(selectExpression.Table);
                    }

                    if (selectExpression.Predicate != null)
                    {
                        builder.AppendLine();
                        builder.Append("WHERE ");

                        Visit(selectExpression.Predicate);
                    }

                    if (selectExpression.Grouping != null)
                    {
                        builder.AppendLine();
                        builder.Append("GROUP BY ");

                        var gatherer = new ProjectionLeafGatheringExpressionVisitor();
                        gatherer.Visit(selectExpression.Grouping);

                        foreach (var (index, alias, expression) in gatherer.GatheredExpressions.Select((p, i) => (i, p.Key, p.Value)))
                        {
                            if (index > 0)
                            {
                                builder.Append(", ");
                            }

                            EmitExpressionListExpression(expression);
                        }
                    }

                    if (selectExpression.OrderBy != null)
                    {
                        builder.AppendLine();
                        builder.Append("ORDER BY ");

                        Visit(selectExpression.OrderBy);
                    }

                    if (selectExpression.Offset != null)
                    {
                        if (selectExpression.OrderBy == null)
                        {
                            builder.AppendLine();
                            builder.Append("ORDER BY (SELECT 1)");
                        }

                        builder.AppendLine();
                        builder.Append("OFFSET ");

                        Visit(selectExpression.Offset);

                        builder.Append(" ROWS");

                        if (selectExpression.Limit != null)
                        {
                            builder.Append(" FETCH NEXT ");

                            Visit(selectExpression.Limit);

                            builder.Append(" ROWS ONLY");
                        }
                    }

                    queryDepth--;

                    return selectExpression.UpdateProjection(
                        new ServerProjectionExpression(
                            Expression.Lambda(selectorBody, readerParameter)));
                }

                case SingleValueRelationalQueryExpression singleValueRelationalQuery
                when singleValueRelationalQuery.Type.IsScalarType():
                {
                    builder.IncreaseIndent();

                    builder.Append("(");
                    builder.AppendLine();

                    Visit(singleValueRelationalQuery.SelectExpression);

                    builder.DecreaseIndent();

                    builder.AppendLine();
                    builder.Append(")");

                    return singleValueRelationalQuery;
                }

                case SingleValueRelationalQueryExpression singleValueRelationalQuery:
                {
                    return VisitComplexTypeSubquery(singleValueRelationalQuery.SelectExpression);
                }

                case EnumerableRelationalQueryExpression enumerableRelationalQuery:
                {
                    return VisitComplexTypeSubquery(enumerableRelationalQuery.SelectExpression);
                }

                case BaseTableExpression baseTable:
                {
                    builder.Append(FormatIdentifier(baseTable.SchemaName));
                    builder.Append(".");
                    builder.Append(FormatIdentifier(baseTable.TableName));
                    builder.Append(" AS ");
                    builder.Append(FormatIdentifier(GetTableAlias(baseTable)));

                    return baseTable;
                }

                case SubqueryTableExpression subquery:
                {
                    builder.IncreaseIndent();

                    builder.Append("(");
                    builder.AppendLine();

                    Visit(subquery.Subquery);

                    builder.DecreaseIndent();

                    builder.AppendLine();
                    builder.Append(") AS ");
                    builder.Append(FormatIdentifier(GetTableAlias(subquery)));

                    return subquery;
                }

                case InnerJoinExpression innerJoin:
                {
                    Visit(innerJoin.OuterTable);

                    builder.AppendLine();
                    builder.Append("INNER JOIN ");

                    Visit(innerJoin.InnerTable);

                    builder.Append(" ON ");

                    Visit(innerJoin.Predicate);

                    return innerJoin;
                }

                case LeftJoinExpression leftJoin:
                {
                    Visit(leftJoin.OuterTable);

                    builder.AppendLine();
                    builder.Append("LEFT JOIN ");

                    Visit(leftJoin.InnerTable);

                    builder.Append(" ON ");

                    Visit(leftJoin.Predicate);

                    return leftJoin;
                }

                case CrossJoinExpression crossJoin:
                {
                    Visit(crossJoin.OuterTable);

                    builder.AppendLine();
                    builder.Append("CROSS JOIN ");

                    Visit(crossJoin.InnerTable);

                    return crossJoin;
                }

                case CrossApplyExpression crossApply:
                {
                    Visit(crossApply.OuterTable);

                    builder.AppendLine();
                    builder.Append("CROSS APPLY ");

                    Visit(crossApply.InnerTable);

                    return crossApply;
                }

                case OuterApplyExpression outerApply:
                {
                    Visit(outerApply.OuterTable);

                    builder.AppendLine();
                    builder.Append("OUTER APPLY ");

                    Visit(outerApply.InnerTable);

                    return outerApply;
                }

                case SetOperatorExpression setOperator:
                {
                    builder.IncreaseIndent();
                    builder.Append("(");
                    builder.AppendLine();

                    Visit(setOperator.Set1);

                    builder.AppendLine();

                    if (setOperator is UnionAllExpression)
                    {
                        builder.Append("UNION ALL");
                    }
                    else if (setOperator is ExceptExpression)
                    {
                        builder.Append("EXCEPT");
                    }
                    else if (setOperator is IntersectExpression)
                    {
                        builder.Append("INTERSECT");
                    }
                    else if (setOperator is UnionExpression)
                    {
                        builder.Append("UNION");
                    }

                    builder.AppendLine();

                    Visit(setOperator.Set2);

                    builder.DecreaseIndent();
                    builder.AppendLine();
                    builder.Append(") AS ");
                    builder.Append(FormatIdentifier(GetTableAlias(setOperator)));

                    return setOperator;
                }

                case SqlColumnExpression sqlColumn:
                {
                    builder.Append(FormatIdentifier(GetTableAlias(sqlColumn.Table)));
                    builder.Append(".");
                    builder.Append(FormatIdentifier(sqlColumn.ColumnName));

                    return sqlColumn;
                }

                case SqlAliasExpression sqlAlias:
                {
                    Visit(sqlAlias.Expression);

                    builder.Append(" AS ");
                    builder.Append(FormatIdentifier(sqlAlias.Alias));

                    return sqlAlias;
                }

                case SqlFragmentExpression sqlFragment:
                {
                    builder.Append(sqlFragment.Fragment);

                    return sqlFragment;
                }

                case SqlFunctionExpression sqlFunction:
                {
                    builder.Append(sqlFunction.FunctionName);
                    builder.Append("(");

                    if (sqlFunction.Arguments.Any())
                    {
                        Visit(sqlFunction.Arguments.First());

                        foreach (var argument in sqlFunction.Arguments.Skip(1))
                        {
                            builder.Append(", ");

                            Visit(argument);
                        }
                    }

                    builder.Append(")");

                    return sqlFunction;
                }

                case SqlCastExpression sqlCast:
                {
                    builder.Append("CAST(");

                    Visit(sqlCast.Expression);

                    builder.Append($" AS {sqlCast.SqlType})");

                    return sqlCast;
                }

                case SqlExistsExpression sqlExists:
                {
                    builder.Append("EXISTS (");

                    builder.IncreaseIndent();

                    builder.AppendLine();

                    Visit(sqlExists.SelectExpression);

                    builder.DecreaseIndent();

                    builder.AppendLine();
                    builder.Append(")");

                    return sqlExists;
                }

                case SqlInExpression sqlIn:
                {
                    Visit(sqlIn.Value);

                    builder.Append(" IN (");

                    switch (sqlIn.Values)
                    {
                        case SelectExpression selectExpression:
                        {
                            builder.IncreaseIndent();
                            builder.AppendLine();

                            Visit(selectExpression);

                            builder.DecreaseIndent();
                            builder.AppendLine();

                            break;
                        }

                        case NewArrayExpression newArrayExpression:
                        {
                            foreach (var (expression, index) in newArrayExpression.Expressions.Select((e, i) => (e, i)))
                            {
                                if (index > 0)
                                {
                                    builder.Append(", ");
                                }

                                Visit(expression);
                            }

                            break;
                        }

                        case ListInitExpression listInitExpression:
                        {
                            foreach (var (elementInit, index) in listInitExpression.Initializers.Select((e, i) => (e, i)))
                            {
                                if (index > 0)
                                {
                                    builder.Append(", ");
                                }

                                Visit(elementInit.Arguments[0]);
                            }

                            break;
                        }

                        case ConstantExpression constantExpression:
                        {
                            var values = from object value in ((IEnumerable)constantExpression.Value)
                                         select Expression.Constant(value);

                            foreach (var (value, index) in values.Select((v, i) => (v, i)))
                            {
                                if (index > 0)
                                {
                                    builder.Append(", ");
                                }

                                Visit(value);
                            }

                            break;
                        }

                        case Expression expression:
                        {
                            var visitor = new ParameterizabilityAnalyzingExpressionVisitor();
                            visitor.Visit(expression);

                            if (!visitor.IsParameterizable)
                            {
                                // TODO: Use better type modelling to avoid this instead.
                                throw new InvalidOperationException();
                            }

                            builder.AddParameterList(expression, FormatParameter);

                            break;
                        }
                    }

                    builder.Append(")");

                    return sqlIn;
                }

                case SqlAggregateExpression sqlAggregate:
                {
                    builder.Append(sqlAggregate.FunctionName);
                    builder.Append("(");

                    if (sqlAggregate.IsDistinct)
                    {
                        builder.Append("DISTINCT ");
                    }

                    Visit(sqlAggregate.Expression);

                    builder.Append(")");

                    return sqlAggregate;
                }

                case OrderByExpression orderBy:
                {
                    if (orderBy is ThenOrderByExpression thenOrderBy)
                    {
                        Visit(thenOrderBy.Previous);

                        builder.Append(", ");
                    }

                    EmitExpressionListExpression(orderBy.Expression);

                    builder.Append(" ");
                    builder.Append(orderBy.Descending ? "DESC" : "ASC");

                    return orderBy;
                }

                default:
                {
                    return base.VisitExtension(node);
                }
            }
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            switch (node.NodeType)
            {
                case ExpressionType.Not:
                {
                    switch (node.Operand)
                    {
                        case SqlExistsExpression sqlExistsExpression:
                        case SqlInExpression sqlInExpression:
                        {
                            builder.Append("NOT ");

                            return base.VisitUnary(node);
                        }

                        default:
                        {
                            return base.Visit(Expression.Equal(Expression.Constant(false), node.Operand));
                        }
                    }
                }
            }

            return base.VisitUnary(node);
        }

        #endregion

        protected virtual Expression VisitComplexTypeSubquery(SelectExpression subquery)
        {
            builder.IncreaseIndent();
            builder.Append("(");
            builder.AppendLine();

            Visit(subquery);

            builder.AppendLine();
            builder.Append("FOR JSON PATH");

            builder.DecreaseIndent();
            builder.AppendLine();
            builder.Append(")");

            return subquery;
        }

        protected virtual void EmitExpressionListExpression(Expression expression)
        {
            if (expression.Type.IsBoolean()
                && !(expression is ConditionalExpression
                    || expression is ConstantExpression
                    || expression is SqlAliasExpression
                    || expression is SqlColumnExpression
                    || expression is SqlCastExpression))
            {
                builder.Append("(CASE WHEN ");

                Visit(expression);

                builder.Append(" THEN 1 ELSE 0 END)");                    
            }
            else
            {
                Visit(expression);
            }
        }

        protected virtual string FormatIdentifier(string identifier)
        {
            return $"[{identifier}]";
        }

        protected virtual string FormatParameter(string name)
        {
            return $"@{name}";
        }

        private string GetTableAlias(AliasedTableExpression table)
        {
            if (!aliasLookup.TryGetValue(table, out var alias))
            {
                alias = table.Alias;

                if (!tableAliases.Add(alias))
                {
                    var i = -1;

                    do
                    {
                        alias = $"{table.Alias}{++i}";
                    }
                    while (!tableAliases.Add(alias));
                }

                aliasLookup.Add(table, alias);
            }

            return alias;
        }

        private static bool IsParameterizable(Expression node)
        {
            if (node == null || !node.Type.IsScalarType())
            {
                return false;
            }

            var visitor = new ParameterizabilityAnalyzingExpressionVisitor();
            visitor.Visit(node);

            return visitor.IsParameterizable;
        }

        private class ParameterizabilityAnalyzingExpressionVisitor : ExpressionVisitor
        {
            private int parameterCount = 0;
            private int extensionCount = 0;

            public bool IsParameterizable => parameterCount > 0 && extensionCount == 0;

            public override Expression Visit(Expression node)
            {
                if (node == null)
                {
                    return null;
                }

                switch (node.NodeType)
                {
                    case ExpressionType.Parameter:
                    {
                        parameterCount++;
                        break;
                    }

                    case ExpressionType.Extension:
                    {
                        extensionCount++;
                        break;
                    }
                }

                return base.Visit(node);
            }
        }

        private static Expression FlattenProjection(
            ProjectionExpression projection, 
            ReaderParameterInjectingExpressionVisitor visitor)
        {
            switch (projection)
            {
                case ServerProjectionExpression serverProjection:
                {
                    return visitor.Inject(serverProjection.ResultLambda.Body);
                }

                case ClientProjectionExpression clientProjection:
                {
                    var client = clientProjection.ResultLambda;
                    var server = clientProjection.ServerLambda;

                    return client.ExpandParameters(visitor.Inject(server.Body));
                }

                case CompositeProjectionExpression compositeProjection:
                {
                    var outer = FlattenProjection(compositeProjection.OuterProjection, visitor);
                    var inner = FlattenProjection(compositeProjection.InnerProjection, visitor);
                    var result = compositeProjection.ResultLambda;

                    return result.ExpandParameters(outer, inner);
                }

                default:
                {
                    throw new InvalidOperationException();
                }
            }
        }

        private class ReaderParameterInjectingExpressionVisitor : ProjectionExpressionVisitor
        {
            public IDictionary<string, Expression> GatheredExpressions { get; private set; } = new Dictionary<string, Expression>();

            private static readonly TypeInfo dbDataReaderTypeInfo
                = typeof(DbDataReader).GetTypeInfo();

            private static readonly MethodInfo getFieldValueMethodInfo
                = dbDataReaderTypeInfo.GetDeclaredMethod(nameof(DbDataReader.GetFieldValue));

            private readonly ParameterExpression readerParameter;
            private int readerIndex;
            private int subLeafIndex;
            private int topLevelIndex;

            protected bool InSubLeaf { get; private set; }

            public ReaderParameterInjectingExpressionVisitor(ParameterExpression readerParameter)
            {
                this.readerParameter = readerParameter;
            }

            public Expression Inject(Expression node)
            {
                if (topLevelIndex > 0)
                {
                    if (topLevelIndex == 1)
                    {
                        GatheredExpressions = GatheredExpressions.Select(p => ("$0." + p.Key, p.Value)).ToDictionary(p => p.Item1, p => p.Item2);
                    }

                    CurrentPath.Push($"${topLevelIndex}");

                    var result = Visit(node);

                    CurrentPath.Pop();

                    topLevelIndex++;

                    return result;
                }

                topLevelIndex++;

                return Visit(node);
            }

            public override Expression Visit(Expression node)
            {
                switch (node)
                {
                    case NewExpression newExpression:
                    case MemberInitExpression memberInitExpression:
                    {
                        var subLeafIndex = this.subLeafIndex;
                        this.subLeafIndex = 0;

                        var visited = base.Visit(node);

                        this.subLeafIndex = subLeafIndex;

                        return visited;
                    }

                    case DefaultIfEmptyExpression defaultIfEmpty:
                    {
                        CurrentPath.Push(defaultIfEmpty.AliasExpression.Alias);
                        var name = string.Join(".", CurrentPath.Reverse());
                        CurrentPath.Pop();

                        GatheredExpressions[name] = defaultIfEmpty.AliasExpression.Expression;

                        return Expression.Condition(
                            test: Expression.Call(
                                readerParameter,
                                getFieldValueMethodInfo.MakeGenericMethod(typeof(bool)),
                                Expression.Constant(readerIndex++)),
                            ifTrue: Expression.Default(defaultIfEmpty.Type),
                            ifFalse: Visit(defaultIfEmpty.Expression));
                    }

                    case MetaAliasExpression metaAliasExpression:
                    {
                        CurrentPath.Push(metaAliasExpression.AliasExpression.Alias);
                        var name = string.Join(".", CurrentPath.Reverse());
                        CurrentPath.Pop();

                        GatheredExpressions[name] = metaAliasExpression.AliasExpression.Expression;

                        return Visit(metaAliasExpression.Expression);
                    }

                    case Expression expression when expression.IsTranslatable() && !IsParameterizable(expression):
                    {
                        if (InSubLeaf)
                        {
                            CurrentPath.Push($"${++subLeafIndex}");
                            var name = string.Join(".", CurrentPath.Reverse());
                            CurrentPath.Pop();
                            GatheredExpressions[name] = expression;
                        }
                        else
                        {
                            var name = string.Join(".", CurrentPath.Reverse());
                            GatheredExpressions[name] = expression;
                        }

                        if (expression is ComplexNestedQueryExpression || expression is EnumerableRelationalQueryExpression)
                        {
                            var type = expression is ComplexNestedQueryExpression
                                ? node.Type
                                : node.Type.FindGenericType(typeof(IEnumerable<>));

                            return Expression.Call(
                                ImpatientExtensions
                                    .GetGenericMethodDefinition((string s) => JsonConvert.DeserializeObject<object>(s))
                                    .MakeGenericMethod(type),
                                Expression.Call(
                                    readerParameter,
                                    getFieldValueMethodInfo.MakeGenericMethod(typeof(string)),
                                    Expression.Constant(readerIndex++)));
                        }

                        return Expression.Call(
                            readerParameter,
                            getFieldValueMethodInfo.MakeGenericMethod(node.Type),
                            Expression.Constant(readerIndex++));
                    }

                    case Expression expression when InLeaf && !InSubLeaf:
                    {
                        InSubLeaf = true;
                        subLeafIndex = 0;

                        var visited = base.Visit(expression);

                        InSubLeaf = false;
                        subLeafIndex = 0;

                        return visited;
                    }

                    default:
                    {
                        return node;
                    }
                }
            }
        }

        private class DbCommandBuilderExpressionBuilder
        {
            private int parameterIndex = 0;
            private int indentationLevel = 0;
            private bool containsParameterList = false;
            private StringBuilder archiveStringBuilder = new StringBuilder();
            private StringBuilder workingStringBuilder = new StringBuilder();

            private readonly ParameterExpression dbCommandVariable = Expression.Parameter(typeof(DbCommand), "command");
            private readonly ParameterExpression stringBuilderVariable = Expression.Parameter(typeof(StringBuilder), "builder");
            private readonly ParameterExpression dbParameterVariable = Expression.Parameter(typeof(DbParameter), "parameter");
            private readonly List<Expression> blockExpressions = new List<Expression>();
            private readonly List<Expression> dbParameterExpressions = new List<Expression>();

            private static readonly MethodInfo stringBuilderAppendMethodInfo
                = typeof(StringBuilder).GetRuntimeMethod(nameof(StringBuilder.Append), new[] { typeof(string) });

            private static readonly MethodInfo stringBuilderToStringMethodInfo
                = typeof(StringBuilder).GetRuntimeMethod(nameof(StringBuilder.ToString), new Type[0]);

            private static readonly MethodInfo stringConcatObjectMethodInfo
                = typeof(string).GetRuntimeMethod(nameof(string.Concat), new[] { typeof(object), typeof(object) });

            private static readonly PropertyInfo dbCommandCommandTextPropertyInfo
                = typeof(DbCommand).GetTypeInfo().GetDeclaredProperty(nameof(DbCommand.CommandText));

            private static readonly PropertyInfo dbCommandParametersPropertyInfo
                = typeof(DbCommand).GetTypeInfo().GetDeclaredProperty(nameof(DbCommand.Parameters));

            private static readonly MethodInfo dbCommandCreateParameterMethodInfo
                = typeof(DbCommand).GetTypeInfo().GetDeclaredMethod(nameof(DbCommand.CreateParameter));

            private static readonly PropertyInfo dbParameterParameterNamePropertyInfo
                = typeof(DbParameter).GetTypeInfo().GetDeclaredProperty(nameof(DbParameter.ParameterName));

            private static readonly PropertyInfo dbParameterValuePropertyInfo
                = typeof(DbParameter).GetTypeInfo().GetDeclaredProperty(nameof(DbParameter.Value));

            private static readonly MethodInfo dbParameterCollectionAddMethodInfo
                = typeof(DbParameterCollection).GetTypeInfo().GetDeclaredMethod(nameof(DbParameterCollection.Add));

            private static readonly MethodInfo enumerableGetEnumeratorMethodInfo
                = typeof(IEnumerable).GetTypeInfo().GetDeclaredMethod(nameof(IEnumerable.GetEnumerator));

            private static readonly MethodInfo enumeratorMoveNextMethodInfo
                = typeof(IEnumerator).GetTypeInfo().GetDeclaredMethod(nameof(IEnumerator.MoveNext));

            private static readonly PropertyInfo enumeratorCurrentPropertyInfo
                = typeof(IEnumerator).GetTypeInfo().GetDeclaredProperty(nameof(IEnumerator.Current));

            public void Append(string sql)
            {
                workingStringBuilder.Append(sql);
            }

            public void AppendLine()
            {
                workingStringBuilder.AppendLine();
                AppendIndent();
            }

            public void EmitSql()
            {
                if (workingStringBuilder.Length > 0)
                {
                    var currentString = workingStringBuilder.ToString();

                    blockExpressions.Add(
                        Expression.Call(
                            stringBuilderVariable,
                            stringBuilderAppendMethodInfo,
                            Expression.Constant(currentString)));

                    archiveStringBuilder.Append(currentString);

                    workingStringBuilder.Clear();
                }
            }

            public void AddParameter(Expression node, Func<string, string> formatter)
            {
                var parameterName = formatter($"p{parameterIndex}");

                Append(parameterName);

                EmitSql();

                var expressions = new Expression[]
                {
                    Expression.Assign(
                        dbParameterVariable,
                        Expression.Call(
                            dbCommandVariable,
                            dbCommandCreateParameterMethodInfo)),
                    Expression.Assign(
                        Expression.MakeMemberAccess(
                            dbParameterVariable,
                            dbParameterParameterNamePropertyInfo),
                        Expression.Constant(parameterName)),
                    Expression.Assign(
                        Expression.MakeMemberAccess(
                            dbParameterVariable,
                            dbParameterValuePropertyInfo),
                        node.Type.GetTypeInfo().IsValueType
                            ? Expression.Convert(node, typeof(object))
                            : node),
                    Expression.Call(
                        Expression.MakeMemberAccess(
                            dbCommandVariable,
                            dbCommandParametersPropertyInfo),
                        dbParameterCollectionAddMethodInfo,
                        dbParameterVariable)
                };

                blockExpressions.AddRange(expressions);
                dbParameterExpressions.AddRange(expressions);

                parameterIndex++;
            }

            public void AddParameterList(Expression node, Func<string, string> formatter)
            {
                var enumeratorVariable = Expression.Parameter(typeof(IEnumerator), "enumerator");
                var indexVariable = Expression.Parameter(typeof(int), "index");
                var parameterPrefixVariable = Expression.Parameter(typeof(string), "parameterPrefix");
                var parameterNameVariable = Expression.Parameter(typeof(string), "parameterName");
                var breakLabel = Expression.Label();

                var parameterListBlock
                    = Expression.Block(
                        new[]
                        {
                            enumeratorVariable,
                            indexVariable,
                            parameterPrefixVariable,
                            parameterNameVariable
                        },
                        Expression.Assign(
                            enumeratorVariable,
                            Expression.Call(
                                node,
                                enumerableGetEnumeratorMethodInfo)),
                        Expression.Assign(
                            parameterPrefixVariable,
                            Expression.Constant(formatter($"p{parameterIndex}_"))),
                        Expression.Loop(
                            @break: breakLabel,
                            body: Expression.Block(
                                Expression.Assign(
                                    parameterNameVariable,
                                    Expression.Call(
                                        stringConcatObjectMethodInfo,
                                        parameterPrefixVariable,
                                        Expression.Convert(indexVariable, typeof(object)))),
                                Expression.IfThenElse(
                                    Expression.Call(enumeratorVariable, enumeratorMoveNextMethodInfo),
                                    Expression.Increment(indexVariable),
                                    Expression.Break(breakLabel)),
                                Expression.IfThen(
                                    Expression.GreaterThan(indexVariable, Expression.Constant(0)),
                                    Expression.Call(
                                        stringBuilderVariable,
                                        stringBuilderAppendMethodInfo,
                                        Expression.Constant(", "))),
                                Expression.Call(
                                    stringBuilderVariable,
                                    stringBuilderAppendMethodInfo,
                                    parameterNameVariable),
                                Expression.Assign(
                                    dbParameterVariable,
                                    Expression.Call(
                                        dbCommandVariable,
                                        dbCommandCreateParameterMethodInfo)),
                                Expression.Assign(
                                    Expression.MakeMemberAccess(
                                        dbParameterVariable,
                                        dbParameterParameterNamePropertyInfo),
                                    parameterNameVariable),
                                Expression.Assign(
                                    Expression.MakeMemberAccess(
                                        dbParameterVariable,
                                        dbParameterValuePropertyInfo),
                                    Expression.MakeMemberAccess(
                                        enumeratorVariable,
                                        enumeratorCurrentPropertyInfo)),
                                Expression.Call(
                                    Expression.MakeMemberAccess(
                                        dbCommandVariable,
                                        dbCommandParametersPropertyInfo),
                                    dbParameterCollectionAddMethodInfo,
                                    dbParameterVariable))));

                EmitSql();
                blockExpressions.Add(parameterListBlock);
                parameterIndex++;
                containsParameterList = true;
            }

            public Expression Build()
            {
                EmitSql();

                var blockVariables = new List<ParameterExpression> { stringBuilderVariable, dbParameterVariable };
                var blockExpressions = this.blockExpressions;

                if (containsParameterList)
                {
                    blockExpressions.Insert(0,
                        Expression.Assign(
                            stringBuilderVariable,
                            Expression.New(typeof(StringBuilder))));

                    blockExpressions.Add(
                        Expression.Assign(
                            Expression.MakeMemberAccess(
                                dbCommandVariable,
                                dbCommandCommandTextPropertyInfo),
                            Expression.Call(
                                stringBuilderVariable,
                                stringBuilderToStringMethodInfo)));
                }
                else
                {
                    blockVariables.Remove(stringBuilderVariable);

                    blockExpressions.Clear();

                    blockExpressions.Add(
                        Expression.Assign(
                            Expression.MakeMemberAccess(
                                dbCommandVariable,
                                dbCommandCommandTextPropertyInfo),
                            Expression.Constant(archiveStringBuilder.ToString())));

                    blockExpressions.AddRange(dbParameterExpressions);

                    if (dbParameterExpressions.Count == 0)
                    {
                        blockVariables.Remove(dbParameterVariable);
                    }
                }

                return Expression.Lambda(
                    typeof(Action<DbCommand>),
                    Expression.Block(blockVariables, blockExpressions),
                    dbCommandVariable);
            }

            public void IncreaseIndent()
            {
                indentationLevel++;
            }

            public void DecreaseIndent()
            {
                indentationLevel--;
            }

            private void AppendIndent()
            {
                workingStringBuilder.Append(string.Empty.PadLeft(indentationLevel * 4));
            }
        }
    }
}