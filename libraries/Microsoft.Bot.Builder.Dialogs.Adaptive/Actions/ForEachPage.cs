﻿// Licensed under the MIT License.
// Copyright (c) Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Expressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Bot.Builder.Dialogs.Adaptive.Actions
{
    /// <summary>
    /// Executes a set of actions once for each item in an in-memory list or collection.
    /// </summary>
    public class ForeachPage : ActionScope
    {
        [JsonProperty("$kind")]
        public const string DeclarativeType = "Microsoft.ForeachPage";

        private const string FOREACHPAGE = "dialog.foreach.page";
        private const string FOREACHPAGEINDEX = "dialog.foreach.pageindex";
        
        private Expression disabled;

        [JsonConstructor]
        public ForeachPage([CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0)
            : base()
        {
            this.RegisterSourceLocation(sourceFilePath, sourceLineNumber);
        }

        /// <summary>
        /// Gets or sets an optional expression which if is true will disable this action.
        /// </summary>
        /// <example>
        /// "user.age > 18".
        /// </example>
        /// <value>
        /// A boolean expression. 
        /// </value>
        [JsonProperty("disabled")]
        public string Disabled
        {
            get { return disabled?.ToString(); }
            set { disabled = value != null ? new ExpressionEngine().Parse(value) : null; }
        }

        // Expression used to compute the list that should be enumerated.
        [JsonProperty("itemsProperty")]
        public string ItemsProperty { get; set; }

        [JsonProperty("pageSize")]
        public int PageSize { get; set; } = 10;

        public override async Task<DialogTurnResult> BeginDialogAsync(DialogContext dc, object options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options is CancellationToken)
            {
                throw new ArgumentException($"{nameof(options)} cannot be a cancellation token");
            }

            if (this.disabled != null && (bool?)this.disabled.TryEvaluate(dc.GetState()).value == true)
            {
                return await dc.EndDialogAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await NextPageAsync(dc, cancellationToken).ConfigureAwait(false);
        }

        protected override async Task<DialogTurnResult> OnEndOfActionsAsync(DialogContext dc, object result = null, CancellationToken cancellationToken = default)
        {
            return await NextPageAsync(dc, cancellationToken).ConfigureAwait(false);
        }

        protected override async Task<DialogTurnResult> OnBreakLoopAsync(DialogContext dc, ActionScopeResult actionScopeResult, CancellationToken cancellationToken = default)
        {
            return await dc.EndDialogAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        protected override async Task<DialogTurnResult> OnContinueLoopAsync(DialogContext dc, ActionScopeResult actionScopeResult, CancellationToken cancellationToken = default)
        {
            return await this.NextPageAsync(dc, cancellationToken).ConfigureAwait(false);
        }

        protected override string OnComputeId()
        {
            return $"{this.GetType().Name}({this.ItemsProperty})";
        }

        private async Task<DialogTurnResult> NextPageAsync(DialogContext dc, CancellationToken cancellationToken)
        {
            Expression itemsProperty = new ExpressionEngine().Parse(this.ItemsProperty);
            int pageIndex = dc.GetState().GetIntValue(FOREACHPAGEINDEX, 0);
            int pageSize = this.PageSize;
            int itemOffset = pageSize * pageIndex;

            var (items, error) = itemsProperty.TryEvaluate(dc.GetState());
            if (error == null)
            {
                var page = this.GetPage(items, itemOffset, pageSize);

                if (page.Any())
                {
                    dc.GetState().SetValue(FOREACHPAGE, page);
                    dc.GetState().SetValue(FOREACHPAGEINDEX, ++pageIndex);
                    return await this.BeginActionAsync(dc, 0, cancellationToken).ConfigureAwait(false);
                }
            }

            // End of list has been reached
            return await dc.EndDialogAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        private List<object> GetPage(object list, int index, int pageSize)
        {
            List<object> page = new List<object>();
            int end = index + pageSize;
            if (list != null && list.GetType() == typeof(JArray))
            {
                for (int i = index; i < end && i < JArray.FromObject(list).Count; i++)
                {
                    page.Add(JArray.FromObject(list)[i]);
                }
            }
            else if (list != null && list is JObject)
            {
                for (int i = index; i < end; i++)
                {
                    if (((JObject)list).SelectToken(i.ToString()).HasValues)
                    {
                        page.Add(((JObject)list).SelectToken(i.ToString()));
                    }
                }
            }

            return page;
        }
    }
}
