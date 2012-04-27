﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Lucene.Net.Documents;
using Lucene.Net.Linq.Mapping;
using Lucene.Net.Linq.Transformation;
using Lucene.Net.Linq.Translation;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.ExpressionTreeVisitors;
using Remotion.Linq.Clauses.ResultOperators;

namespace Lucene.Net.Linq
{
    internal class QueryExecutor<TDocument> : LuceneQueryExecutor<TDocument>
    {
        private readonly Func<TDocument> newItem;
        private readonly IDocumentMapper<TDocument> mapper;

        public QueryExecutor(Directory directory, Context context, Func<TDocument> newItem, IDocumentMapper<TDocument> mapper)
            : base(directory, context)
        {
            this.newItem = newItem;
            this.mapper = mapper;
        }

        protected override void SetCurrentDocument(Document doc)
        {
            var item = newItem();

            mapper.ToObject(doc, item);

            CurrentDocument = item;
        }

        public override IFieldMappingInfo GetMappingInfo(string propertyName)
        {
            return mapper.GetMappingInfo(propertyName);
        }
    }

    internal abstract class LuceneQueryExecutor<TDocument> : IQueryExecutor, IFieldMappingInfoProvider
    {
        private readonly Directory directory;
        private readonly Context context;

        public TDocument CurrentDocument { get; protected set; }

        protected LuceneQueryExecutor(Directory directory, Context context)
        {
            this.directory = directory;
            this.context = context;
        }

        public T ExecuteScalar<T>(QueryModel queryModel)
        {
            var builder = PrepareQuery(queryModel);

            using (var searcher = new IndexSearcher(directory, true))
            {
                var skipResults = builder.SkipResults;
                var maxResults = Math.Min(builder.MaxResults, searcher.MaxDoc() - skipResults);

                var hits = searcher.Search(builder.Query, null, maxResults, builder.Sort);

                var projection = GetScalarProjector<T>(builder.ResultSetOperator, hits);
                var projector = projection.Compile();

                return projector(hits);
            }
        }

        public T ExecuteSingle<T>(QueryModel queryModel, bool returnDefaultWhenEmpty)
        {
            var sequence = ExecuteCollection<T>(queryModel);

            return returnDefaultWhenEmpty ? sequence.SingleOrDefault() : sequence.Single();
        }

        public IEnumerable<T> ExecuteCollection<T>(QueryModel queryModel)
        {
            var builder = PrepareQuery(queryModel);

            var projection = GetProjector<T>(queryModel);
            var projector = projection.Compile();
            
            using (var searcher = new IndexSearcher(directory, true))
            {
                var skipResults = builder.SkipResults;
                var maxResults = Math.Min(builder.MaxResults, searcher.MaxDoc() - skipResults);
                
                var hits = searcher.Search(builder.Query, null, maxResults + skipResults, builder.Sort);

                for (var i = skipResults; i < hits.ScoreDocs.Length; i++)
                {
                    SetCurrentDocument(searcher.Doc(hits.ScoreDocs[i].doc));
                    yield return projector(CurrentDocument);
                }
            }
        }

        private QueryModelTranslator PrepareQuery(QueryModel queryModel)
        {
            QueryModelTransformer.TransformQueryModel(queryModel);

            var builder = new QueryModelTranslator(context, this);
            builder.Build(queryModel);

#if DEBUG
            System.Diagnostics.Trace.WriteLine("Lucene query: " + builder.Query + " sort: " + builder.Sort, "Lucene.Net.Linq");
#endif

            var mapping = new QuerySourceMapping();
            mapping.AddMapping(queryModel.MainFromClause, GetCurrentRowExpression());
            queryModel.TransformExpressions(e => ReferenceReplacingExpressionTreeVisitor.ReplaceClauseReferences(e, mapping, true));
            return builder;
        }

        public abstract IFieldMappingInfo GetMappingInfo(string propertyName);

        protected abstract void SetCurrentDocument(Document doc);

        protected virtual Expression GetCurrentRowExpression()
        {
            return Expression.Property(Expression.Constant(this), "CurrentDocument");
        }

        protected virtual Expression<Func<TDocument, T>> GetProjector<T>(QueryModel queryModel)
        {
            return Expression.Lambda<Func<TDocument, T>>(queryModel.SelectClause.Selector, Expression.Parameter(typeof(TDocument)));
        }

        protected virtual Expression<Func<TopFieldDocs, T>> GetScalarProjector<T>(ResultOperatorBase op, TopFieldDocs docs)
        {
            Expression call = Expression.Call(Expression.Constant(this), GetType().GetMethod("DoCount"), Expression.Constant(docs));
            if (op is LongCountResultOperator)
            {
                call = Expression.Convert(call, typeof(long));
            }
            else if (!(op is CountResultOperator))
            {
                throw new NotSupportedException("The result operator type " + op.GetType() + " is not supported.");
            }
            return Expression.Lambda<Func<TopFieldDocs, T>>(call, Expression.Parameter(typeof(TopFieldDocs)));
        }

        public int DoCount(TopFieldDocs d)
        {
            return d.ScoreDocs.Length;
        }

    }
}