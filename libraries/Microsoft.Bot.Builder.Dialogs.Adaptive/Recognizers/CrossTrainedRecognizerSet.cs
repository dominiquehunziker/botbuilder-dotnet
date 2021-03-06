﻿// Licensed under the MIT License.
// Copyright (c) Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Microsoft.Bot.Schema;
using Microsoft.Recognizers.Text.NumberWithUnit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Bot.Builder.Dialogs.Adaptive.Recognizers
{
    /// <summary>
    /// CrossTrainedRecognizerSet - Recognizer for selecting between cross trained recognizers.
    /// </summary>
    /// <remarks>
    /// Recognizer implementation which calls multiple recognizers that are cross trained with intents
    /// that model deferring to another recognizer. Each recognizer should have intents 
    /// with special intent name pattern $"DefersToRecognizer_{Id}" to represent a cross-trained 
    /// intent for another recognizer.
    /// 
    /// If there is consensus among the cross trained recognizers, the recognizerResult structure from
    /// the consensus recognizer is returned.
    /// 
    /// In the case that there is conflicting or ambigious signals from the recognizers then an 
    /// intent of "ChooseIntent" will be returned with the results of all of the recognizers.
    /// </remarks>
    public class CrossTrainedRecognizerSet : Recognizer
    {
        [JsonProperty("$kind")]
        public const string DeclarativeType = "Microsoft.CrossTrainedRecognizerSet";

        /// <summary>
        /// Standard cross trained intent name prefix.
        /// </summary>
        public const string DeferPrefix = "DeferToRecognizer_";

        /// <summary>
        /// Intent name that will be produced by this recognizer if the child recognizers do not have consensus for intents.
        /// </summary>
        public const string ChooseIntent = "ChooseIntent";

        /// <summary>
        /// Standard none intent that means none of the recognizers recognize the intent.
        /// </summary>
        /// <remarks>
        /// If each recognizer returns no intents or None intents, then this recognizer will return None intent.
        /// </remarks>
        public const string NoneIntent = "None";

        [JsonConstructor]
        public CrossTrainedRecognizerSet()
        {
        }

        /// <summary>
        /// Gets or sets the input recognizers.
        /// </summary>
        /// <value>
        /// The input recognizers.
        /// </value>
        [JsonProperty("recognizers")]
        public List<Recognizer> Recognizers { get; set; } = new List<Recognizer>();

        public override async Task<RecognizerResult> RecognizeAsync(DialogContext dialogContext, CancellationToken cancellationToken = default)
        {
            if (dialogContext == null)
            {
                throw new ArgumentNullException(nameof(dialogContext));
            }

            EnsureRecognizerIds();

            // run all of the recognizers in parallel
            var results = await Task.WhenAll(Recognizers.Select(r => r.RecognizeAsync(dialogContext, cancellationToken)));

            return ProcessResults(results);
        }

        public override async Task<RecognizerResult> RecognizeAsync(DialogContext dialogContext, Activity activity, CancellationToken cancellationToken = default)
        {
            if (dialogContext == null)
            {
                throw new ArgumentNullException(nameof(dialogContext));
            }

            if (activity == null)
            {
                throw new ArgumentNullException(nameof(activity));
            }

            EnsureRecognizerIds();

            // run all of the recognizers in parallel
            var results = await Task.WhenAll(Recognizers.Select(r => r.RecognizeAsync(dialogContext, activity, cancellationToken)));

            return ProcessResults(results);
        }

        public override async Task<RecognizerResult> RecognizeAsync(DialogContext dialogContext, string text, string locale = null, CancellationToken cancellationToken = default)
        {
            if (dialogContext == null)
            {
                throw new ArgumentNullException(nameof(dialogContext));
            }

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            EnsureRecognizerIds();

            // run all of the recognizers in parallel
            var results = await Task.WhenAll(Recognizers.Select(r => r.RecognizeAsync(dialogContext, text, locale, cancellationToken)));

            return ProcessResults(results);
        }

        private RecognizerResult ProcessResults(RecognizerResult[] results)
        {
            // put results into a dictionary for easier lookup while processing.
            var recognizerResults = new Dictionary<string, RecognizerResult>(System.StringComparer.OrdinalIgnoreCase);
            var intents = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            
            string text = string.Empty;
            for (int iRecognizer = 0; iRecognizer < Recognizers.Count; iRecognizer++)
            {
                var recognizer = Recognizers[iRecognizer];
                var result = results[iRecognizer];
                recognizerResults[recognizer.Id] = result;
                var (topIntent, score) = result.GetTopScoringIntent();
                intents[recognizer.Id] = topIntent;
                text = result.Text ?? string.Empty;
            }

            // this is the consensusRecognizer to use
            string consensusRecognizerId = null;
            foreach (var recognizer in Recognizers)
            {
                var intent = intents[recognizer.Id];

                if (!IsRedirect(intent))
                {
                    // we have a real intent and it's the first one we found.
                    if (consensusRecognizerId == null)
                    {
                        consensusRecognizerId = recognizer.Id;
                    }
                    else
                    {
                        // we have a second recognizer with an intent

                        // if one of them is None intent, then go with the other one.
                        if (intent == NoneIntent)
                        {
                            // then we are fine with the one we have, just ignore this one
                            continue;
                        }
                        else if (intents[consensusRecognizerId] == "None")
                        {
                            // then we can drop the old one and go with the new one instead
                            consensusRecognizerId = recognizer.Id;
                        }
                        else
                        {
                            // ambigious because of 2 real intents, and neither are None so return AmbigiousIntent
                            return CreateChooseIntentResult(text, recognizerResults);
                        }
                    }
                }
                else
                {
                    // get the redirectId and redirectIntent 
                    var redirectId = GetRedirectId(intent);
                    var redirectIntent = intents[redirectId];

                    // if the redirectIntent is itself a redirect, then we have double redirect which means disagreement.
                    if (IsRedirect(redirectIntent))
                    {
                        // we have ambiguity, return AmbigiousIntent
                        return CreateChooseIntentResult(text, recognizerResults);
                    }
                }
            }

            // we have consensus for consensusRecognizer, return the results of that recognizer as the result.
            return recognizerResults[consensusRecognizerId];
        }

        private RecognizerResult CreateChooseIntentResult(string text, Dictionary<string, RecognizerResult> recognizerResults)
        {
            // create IntentScore with { "recognizerId" : { ...RecognizerResult.. } }
            var chooseIntentResult = new IntentScore()
            {
                Score = 0.5F
            };

            var intents = new JObject();
            foreach (var recognizerResult in recognizerResults.Values)
            {
                var (topIntent, score) = recognizerResult.GetTopScoringIntent();
                chooseIntentResult.Properties[topIntent] = recognizerResult;
            }

            return new RecognizerResult()
            {
                Text = text,
                Intents = new Dictionary<string, IntentScore>()
                {
                    { ChooseIntent, (IntentScore)chooseIntentResult }
                }
            };
        }

        private bool IsRedirect(string intent)
        {
            return intent.StartsWith(DeferPrefix);
        }

        private string GetRedirectId(string intent)
        {
            return intent.Substring(DeferPrefix.Length);
        }

        private void EnsureRecognizerIds()
        {
            if (this.Recognizers.Any(recognizer => string.IsNullOrEmpty(recognizer.Id)))
            {
                throw new ArgumentNullException("This recognizer requires that each recognizer in the set have an .Id value.");
            }
        }
    }
}
