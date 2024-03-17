﻿using LLama.Abstractions;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LLama
{
    /// <summary>
    /// The base class for stateful LLama executors.
    /// </summary>
    public abstract class StatefulExecutorBase : ILLamaExecutor
    {
        /// <summary>
        /// The logger used by this executor.
        /// </summary>
        protected ILogger? _logger;
        /// <summary>
        /// The tokens that were already processed by the model.
        /// </summary>
        protected int _pastTokensCount; // n_past
        /// <summary>
        /// The tokens that were consumed by the model during the current inference.
        /// </summary>
        protected int _consumedTokensCount; // n_consume
        /// <summary>
        /// 
        /// </summary>
        protected int _n_session_consumed;
        /// <summary>
        /// 
        /// </summary>
        protected int _n_matching_session_tokens;
        /// <summary>
        /// The path of the session file.
        /// </summary>
        protected string? _pathSession;
        /// <summary>
        /// A container of the tokens to be processed and after processed.
        /// </summary>
        protected List<LLamaToken> _embeds = new(); // embd
        /// <summary>
        /// A container for the tokens of input.
        /// </summary>
        protected List<LLamaToken> _embed_inps = new();
        /// <summary>
        /// 
        /// </summary>
        protected List<LLamaToken> _session_tokens = new();
        /// <summary>
        /// The last tokens generated by the model.
        /// </summary>
        protected FixedSizeQueue<LLamaToken> _last_n_tokens;
        /// <summary>
        /// The context used by the executor.
        /// </summary>
        public LLamaContext Context { get; }

        /// <summary>
        /// Current "mu" value for mirostat sampling
        /// </summary>
        protected float? MirostatMu { get; set; }

        private readonly StreamingTokenDecoder _decoder;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        protected StatefulExecutorBase(LLamaContext context, ILogger? logger = null)
        {
            _logger = logger;
            Context = context;
            _pastTokensCount = 0;
            _consumedTokensCount = 0;
            _n_session_consumed = 0;
            _last_n_tokens = new FixedSizeQueue<LLamaToken>((int)Context.ContextSize);
            _decoder = new StreamingTokenDecoder(context);
        }

        /// <summary>
        /// This API is currently not verified.
        /// </summary>
        /// <param name="filename"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="RuntimeError"></exception>
        public StatefulExecutorBase WithSessionFile(string filename)
        {
            _pathSession = filename;
            if (string.IsNullOrEmpty(filename))
            {
                throw new ArgumentNullException(nameof(filename), "File name cannot be empty.");
            }
            if (File.Exists(filename))
            {
                _logger?.LogInformation($"[LLamaExecutor] Attempting to load saved session from {filename}");
                var session_tokens = new LLamaToken[Context.ContextSize];
                if (!NativeApi.llama_load_session_file(Context.NativeHandle, _pathSession, session_tokens, (ulong)Context.ContextSize, out var n_token_count_out))
                {
                    _logger?.LogError($"[LLamaExecutor] Failed to load session file {filename}");
                    throw new RuntimeError($"Failed to load session file {_pathSession}");
                }
                _session_tokens = session_tokens.Take((int)n_token_count_out).ToList();
                _logger?.LogInformation($"[LLamaExecutor] Loaded a session with prompt size of {session_tokens.Length} tokens");
            }
            else
            {
                _logger?.LogWarning("[LLamaExecutor] Session file does not exist, will create");
            }

            _n_matching_session_tokens = 0;
            if (_session_tokens.Count > 0)
            {
                foreach (var id in _session_tokens)
                {
                    if (_n_matching_session_tokens >= _embed_inps.Count || id != _embed_inps[_n_matching_session_tokens])
                    {
                        break;
                    }
                    _n_matching_session_tokens++;
                }
                if (_n_matching_session_tokens >= _embed_inps.Count)
                {
                    _logger?.LogInformation("[LLamaExecutor] Session file has exact match for prompt!");
                }
                else if (_n_matching_session_tokens < _embed_inps.Count / 2)
                {
                    _logger?.LogWarning($"[LLamaExecutor] Session file has low similarity to prompt ({_n_matching_session_tokens} / {_embed_inps.Count} tokens) will mostly be reevaluated");
                }
                else
                {
                    _logger?.LogInformation($"[LLamaExecutor] Session file matches {_n_matching_session_tokens} / {_embed_inps.Count} tokens of prompt");
                }
            }

            return this;
        }

        /// <summary>
        /// This API has not been verified currently.
        /// </summary>
        /// <param name="filename"></param>
        public void SaveSessionFile(string filename)
        {
            var session_token_array = _session_tokens.ToArray();
            NativeApi.llama_save_session_file(Context.NativeHandle, filename, session_token_array, (ulong)session_token_array.Length);
        }

        /// <summary>
        /// After running out of the context, take some tokens from the original prompt and recompute the logits in batches.
        /// </summary>
        /// <param name="tokensToKeep"></param>
        protected virtual void HandleRunOutOfContext(int tokensToKeep)
        {
            // if we run out of context:
            // - take the tokensToKeep first tokens from the original prompt (via n_past)
            // - take half of the last (n_ctx - tokensToKeep) tokens and recompute the logits in batches
            int n_left = _pastTokensCount - tokensToKeep;

            _pastTokensCount = Math.Max(1, tokensToKeep);

            // insert n_left/2 tokens at the start of embed from last_n_tokens
            _embeds.InsertRange(0, _last_n_tokens.Take(_last_n_tokens.Count - _embeds.Count).Skip((int)Context.ContextSize - n_left / 2 - _embeds.Count));

            // stop saving session if we run out of context
            _pathSession = string.Empty;
        }

        /// <summary>
        /// Try to reuse the matching prefix from the session file.
        /// </summary>
        protected virtual void TryReuseMathingPrefix()
        {
            if (_n_session_consumed < _session_tokens.Count)
            {
                int i = 0;
                for (; i < _embeds.Count; i++)
                {
                    if (_embeds[i] != _session_tokens[_n_session_consumed])
                    {
                        _session_tokens = _session_tokens.Take(_n_session_consumed).ToList();
                        break;
                    }

                    _pastTokensCount++;
                    _n_session_consumed++;

                    if (_n_session_consumed >= _session_tokens.Count)
                    {
                        i++;
                        break;
                    }
                }

                if (i > 0)
                {
                    _embeds.RemoveRange(0, i);
                }
            }
        }

        /// <summary>
        /// Decide whether to continue the loop.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        protected abstract Task<bool> GetLoopCondition(InferStateArgs args);

        /// <summary>
        /// Preprocess the inputs before the inference.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="args"></param>
        protected abstract Task PreprocessInputs(string text, InferStateArgs args);

        /// <summary>
        /// Do some post processing after the inference.
        /// </summary>
        /// <param name="inferenceParams"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        protected abstract Task<(bool, IReadOnlyList<string>)> PostProcess(IInferenceParams inferenceParams, InferStateArgs args);

        /// <summary>
        /// The core inference logic.
        /// </summary>
        /// <param name="inferenceParams"></param>
        /// <param name="args"></param>
        protected abstract Task InferInternal(IInferenceParams inferenceParams, InferStateArgs args);

        /// <summary>
        /// Save the current state to a file.
        /// </summary>
        /// <param name="filename"></param>
        public abstract Task SaveState(string filename);

        /// <summary>
        /// Get the current state data.
        /// </summary>
        /// <returns></returns>
        public abstract ExecutorBaseState GetStateData();

        /// <summary>
        /// Load the state from data.
        /// </summary>
        /// <param name="data"></param>
        public abstract Task LoadState(ExecutorBaseState data);

        /// <summary>
        /// Load the state from a file.
        /// </summary>
        /// <param name="filename"></param>
        public abstract Task LoadState(string filename);


        /// <summary>
        /// Execute the inference.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="inferenceParams"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public virtual async IAsyncEnumerable<string> InferAsync(string text, IInferenceParams? inferenceParams = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inferenceParams ??= new InferenceParams();

            var args = new InferStateArgs
            {
                Antiprompts = inferenceParams.AntiPrompts.ToList(),
                RemainedTokens = inferenceParams.MaxTokens,
                ReturnValue = false,
                WaitForInput = false,
                NeedToSaveSession = !string.IsNullOrEmpty(_pathSession) && _n_matching_session_tokens < _embed_inps.Count
            };

            await PreprocessInputs(text, args);

            while (await GetLoopCondition(args))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                await InferInternal(inferenceParams, args);

                if (args.ReturnValue)
                {
                    _decoder.AddRange(_embeds);
                    yield return _decoder.Read();
                }

                var (breakGeneration, extraOutputs) = await PostProcess(inferenceParams, args);
                if (extraOutputs is { Count: > 0 })
                {
                    foreach (var item in extraOutputs)
                    {
                        yield return item;
                    }
                }
                if (breakGeneration)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Asynchronously runs a prompt through the model to compute KV cache without generating any new tokens.
        /// It could reduce the latency of the first time response if the first input from the user is not immediate.
        /// </summary>
        /// <param name="prompt">Prompt to process</param>
        /// <returns></returns>
        public virtual async Task PrefillPromptAsync(string prompt)
        {
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 0
            };
            var args = new InferStateArgs
            {
                Antiprompts = new List<string>(),
                RemainedTokens = 0,
                ReturnValue = false,
                WaitForInput = true,
                NeedToSaveSession = false
            };

            await PreprocessInputs(prompt, args);
            // First run adds the prompt to the _embeds
            await InferInternal(inferenceParams, args);
            // Second run puts it through decode
            await InferInternal(inferenceParams, args);
        }   

        /// <summary>
        /// State arguments that are used in single inference
        /// </summary>
        protected class InferStateArgs
        {
            /// <summary>
            /// 
            /// </summary>
            public IList<string>? Antiprompts { get; set; }
            /// <summary>
            /// Tokens count remained to be used. (n_remain)
            /// </summary>
            public int RemainedTokens { get; set; }
            /// <summary>
            /// 
            /// </summary>
            public bool ReturnValue { get; set; }
            /// <summary>
            /// 
            /// </summary>
            public bool WaitForInput { get; set; }
            /// <summary>
            /// 
            /// </summary>
            public bool NeedToSaveSession { get; set; }
        }

        public class ExecutorBaseState
        {
            [JsonPropertyName("n_past")]
            public int PastTokensCount { get; set; }

            [JsonPropertyName("n_consumed")]
            public int ConsumedTokensCount { get; set; }

            [JsonPropertyName("n_session_consumed")]
            public int ConsumedSessionCount { get; set; }

            [JsonPropertyName("n_matching_session_tokens")]
            public int MatchingSessionTokensCount { get; set; }

            [JsonPropertyName("path_session")]
            public string? SessionFilePath { get; set; }

            [JsonPropertyName("embd")]
            public LLamaToken[] Embeds { get; set; }

            [JsonPropertyName("embd_inps")]
            public LLamaToken[] EmbedInps { get; set; }

            [JsonPropertyName("session_tokens")]
            public LLamaToken[] SessionTokens { get; set; }

            [JsonPropertyName("last_n_tokens")]
            public LLamaToken[] LastTokens { get; set; }

            [JsonPropertyName("last_tokens_maximum_count")]
            public int LastTokensCapacity { get; set; }

            [JsonPropertyName("mirostat_mu")]
            public float? MirostatMu { get; set; }
        }
    }
}
