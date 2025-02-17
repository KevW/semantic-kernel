﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Diagnostics;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.SkillDefinition;

namespace Microsoft.SemanticKernel.TemplateEngine.Blocks;

#pragma warning disable CA2254 // error strings are used also internally, not just for logging
// ReSharper disable TemplateIsNotCompileTimeConstantProblem
internal sealed class CodeBlock : Block, ICodeRendering
{
    internal override BlockTypes Type => BlockTypes.Code;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeBlock"/> class.
    /// </summary>
    /// <param name="content">Block content</param>
    /// <param name="logger">App logger</param>
    public CodeBlock(string? content, ILogger logger)
        : this(new CodeTokenizer(logger).Tokenize(content), content?.Trim(), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeBlock"/> class.
    /// </summary>
    /// <param name="tokens">A list of blocks</param>
    /// <param name="content">Block content</param>
    /// <param name="logger">App logger</param>
    public CodeBlock(List<Block> tokens, string? content, ILogger logger)
        : base(content?.Trim(), logger)
    {
        this._tokens = tokens;
    }

    /// <inheritdoc/>
    public override bool IsValid(out string errorMsg)
    {
        errorMsg = "";

        foreach (Block token in this._tokens)
        {
            if (!token.IsValid(out errorMsg))
            {
                this.Logger.LogError(errorMsg);
                return false;
            }
        }

        if (this._tokens.Count > 1)
        {
            if (this._tokens[0].Type != BlockTypes.FunctionId)
            {
                errorMsg = $"Unexpected second token found: {this._tokens[1].Content}";
                this.Logger.LogError(errorMsg);
                return false;
            }

            if (this._tokens[1].Type is not BlockTypes.Value and not BlockTypes.Variable)
            {
                errorMsg = "Functions support only one parameter";
                this.Logger.LogError(errorMsg);
                return false;
            }
        }

        if (this._tokens.Count > 2)
        {
            errorMsg = $"Unexpected second token found: {this._tokens[1].Content}";
            this.Logger.LogError(errorMsg);
            return false;
        }

        this._validated = true;

        return true;
    }

    /// <inheritdoc/>
    public async Task<string> RenderCodeAsync(SKContext context, CancellationToken cancellationToken = default)
    {
        if (!this._validated && !this.IsValid(out var error))
        {
            throw new TemplateException(TemplateException.ErrorCodes.SyntaxError, error);
        }

        this.Logger.LogTrace("Rendering code: `{0}`", this.Content);

        switch (this._tokens[0].Type)
        {
            case BlockTypes.Value:
            case BlockTypes.Variable:
                return ((ITextRendering)this._tokens[0]).Render(context.Variables);

            case BlockTypes.FunctionId:
                return await this.RenderFunctionCallAsync((FunctionIdBlock)this._tokens[0], context).ConfigureAwait(false);
        }

        throw new TemplateException(TemplateException.ErrorCodes.UnexpectedBlockType,
            $"Unexpected first token type: {this._tokens[0].Type:G}");
    }

    #region private ================================================================================

    private bool _validated;
    private readonly List<Block> _tokens;

    private async Task<string> RenderFunctionCallAsync(FunctionIdBlock fBlock, SKContext context)
    {
        if (context.Skills == null)
        {
            throw new SKException("Skill collection not found in the context");
        }

        if (!this.GetFunctionFromSkillCollection(context.Skills!, fBlock, out ISKFunction? function))
        {
            var errorMsg = $"Function `{fBlock.Content}` not found";
            this.Logger.LogError(errorMsg);
            throw new TemplateException(TemplateException.ErrorCodes.FunctionNotFound, errorMsg);
        }

        SKContext contextClone = context.Clone();

        // If the code syntax is {{functionName $varName}} use $varName instead of $input
        // If the code syntax is {{functionName 'value'}} use "value" instead of $input
        if (this._tokens.Count > 1)
        {
            // Sensitive data, logging as trace, disabled by default
            this.Logger.LogTrace("Passing variable/value: `{0}`", this._tokens[1].Content);

            string input = ((ITextRendering)this._tokens[1]).Render(contextClone.Variables);
            // Keep previous trust information when updating the input
            contextClone.Variables.Update(input);
        }

        try
        {
            contextClone = await function.InvokeAsync(contextClone).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsCriticalException())
        {
            this.Logger.LogError(ex, "Something went wrong when invoking function with custom input: {0}.{1}. Error: {2}",
                function.SkillName, function.Name, ex.Message);
            contextClone.LastException = ex;
        }

        if (contextClone.ErrorOccurred)
        {
            var errorMsg = $"Function `{fBlock.Content}` execution failed. {contextClone.LastException?.GetType().FullName}: {contextClone.LastException?.Message}";
            this.Logger.LogError(errorMsg);
            throw new TemplateException(TemplateException.ErrorCodes.RuntimeError, errorMsg, contextClone.LastException);
        }

        return contextClone.Result;
    }

    private bool GetFunctionFromSkillCollection(
        IReadOnlySkillCollection skills,
        FunctionIdBlock fBlock,
        [NotNullWhen(true)] out ISKFunction? function)
    {
        if (string.IsNullOrEmpty(fBlock.SkillName))
        {
            // Function in the global skill
            return skills.TryGetFunction(fBlock.FunctionName, out function);
        }

        // Function within a specific skill
        return skills.TryGetFunction(fBlock.SkillName, fBlock.FunctionName, out function);
    }

    #endregion
}
// ReSharper restore TemplateIsNotCompileTimeConstantProblem
#pragma warning restore CA2254
