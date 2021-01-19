﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Snippets;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Completion.Providers
{
    [ExportCompletionProvider(nameof(SnippetCompletionProvider), LanguageNames.CSharp)]
    [ExtensionOrder(After = nameof(CrefCompletionProvider))]
    [Shared]
    internal sealed class SnippetCompletionProvider : LSPCompletionProvider
    {
        internal override bool IsSnippetProvider => true;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public SnippetCompletionProvider()
        {
        }

        public override bool IsInsertionTrigger(SourceText text, int characterPosition, OptionSet options)
            => CompletionUtilities.IsTriggerCharacter(text, characterPosition, options);

        public override ImmutableHashSet<char> TriggerCharacters { get; } = CompletionUtilities.CommonTriggerCharacters;

        public override async Task ProvideCompletionsAsync(CompletionContext context)
        {
            try
            {
                var document = context.Document;
                var position = context.Position;
                var options = context.Options;
                var cancellationToken = context.CancellationToken;

                using (Logger.LogBlock(FunctionId.Completion_SnippetCompletionProvider_GetItemsWorker_CSharp, cancellationToken))
                {
                    // TODO (https://github.com/dotnet/roslyn/issues/5107): Enable in Interactive.
                    var workspace = document.Project.Solution.Workspace;
                    if (!workspace.CanApplyChange(ApplyChangesKind.ChangeDocument) ||
                         workspace.Kind == WorkspaceKind.Debugger ||
                         workspace.Kind == WorkspaceKind.Interactive)
                    {
                        return;
                    }

                    var snippetCompletionItems = await document.GetUnionItemsFromDocumentAndLinkedDocumentsAsync(
                        UnionCompletionItemComparer.Instance,
                        (d, c) => GetSnippetsForDocumentAsync(d, position, workspace, c),
                        cancellationToken).ConfigureAwait(false);

                    context.AddItems(snippetCompletionItems);
                }
            }
            catch (Exception e) when (FatalError.ReportAndCatchUnlessCanceled(e))
            {
                // nop
            }
        }

        private static async Task<ImmutableArray<CompletionItem>> GetSnippetsForDocumentAsync(
            Document document, int position, Workspace workspace, CancellationToken cancellationToken)
        {
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();
            var semanticFacts = document.GetLanguageService<ISemanticFactsService>();

            var leftToken = syntaxTree.GetRoot(cancellationToken).FindTokenOnLeftOfPosition(position, includeDirectives: true);
            var targetToken = leftToken.GetPreviousTokenIfTouchingWord(position);

            if (syntaxFacts.IsInNonUserCode(syntaxTree, position, cancellationToken) ||
                syntaxTree.IsRightOfDotOrArrowOrColonColon(position, targetToken, cancellationToken) ||
                syntaxFacts.GetContainingTypeDeclaration(await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false), position) is EnumDeclarationSyntax)
            {
                return default;
            }

            var isPossibleTupleContext = syntaxFacts.IsPossibleTupleContext(syntaxTree, position, cancellationToken);

            if (syntaxFacts.IsPreProcessorDirectiveContext(syntaxTree, position, cancellationToken))
            {
                var directive = leftToken.GetAncestor<DirectiveTriviaSyntax>();
                if (!directive.DirectiveNameToken.IsKind(
                        SyntaxKind.IfKeyword,
                        SyntaxKind.RegionKeyword,
                        SyntaxKind.ElseKeyword,
                        SyntaxKind.ElifKeyword,
                        SyntaxKind.ErrorKeyword,
                        SyntaxKind.LineKeyword,
                        SyntaxKind.PragmaKeyword,
                        SyntaxKind.EndIfKeyword,
                        SyntaxKind.UndefKeyword,
                        SyntaxKind.EndRegionKeyword,
                        SyntaxKind.WarningKeyword))
                {
                    var semanticModel = await document.ReuseExistingSpeculativeModelAsync(position, cancellationToken).ConfigureAwait(false);
                    return await GetSnippetCompletionItemsAsync(workspace, semanticModel, isPreProcessorContext: true,
                            isTupleContext: isPossibleTupleContext, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var semanticModel = await document.ReuseExistingSpeculativeModelAsync(position, cancellationToken).ConfigureAwait(false);

                if (semanticFacts.IsGlobalStatementContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsExpressionContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsStatementContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsTypeContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsTypeDeclarationContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsNamespaceContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsNamespaceDeclarationNameContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsMemberDeclarationContext(semanticModel, position, cancellationToken) ||
                    semanticFacts.IsLabelContext(semanticModel, position, cancellationToken))
                {
                    return await GetSnippetCompletionItemsAsync(workspace, semanticModel, isPreProcessorContext: false,
                        isTupleContext: isPossibleTupleContext, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            return default;
        }

        private static readonly CompletionItemRules s_tupleRules = CompletionItemRules.Default.
          WithCommitCharacterRule(CharacterSetModificationRule.Create(CharacterSetModificationKind.Remove, ':'));

        private static async Task<ImmutableArray<CompletionItem>> GetSnippetCompletionItemsAsync(
            Workspace workspace, SemanticModel semanticModel, bool isPreProcessorContext, bool isTupleContext, CancellationToken cancellationToken)
        {
            var service = workspace.Services.GetLanguageServices(semanticModel.Language).GetService<ISnippetInfoService>();
            if (service == null)
                return default;

            var snippets = service.GetSnippetsIfAvailable();
            if (isPreProcessorContext)
            {
                snippets = snippets.Where(snippet => snippet.Shortcut != null && snippet.Shortcut.StartsWith("#", StringComparison.Ordinal));
            }
            var text = await semanticModel.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);

            return snippets.SelectAsArray(snippet =>
            {
                var rules = isTupleContext ? s_tupleRules : CompletionItemRules.Default;
                rules = rules.WithFormatOnCommit(service.ShouldFormatSnippet(snippet));

                return CommonCompletionItem.Create(
                                displayText: isPreProcessorContext ? snippet.Shortcut.Substring(1) : snippet.Shortcut,
                                displayTextSuffix: "",
                                sortText: isPreProcessorContext ? snippet.Shortcut.Substring(1) : snippet.Shortcut,
                                description: (snippet.Title + Environment.NewLine + snippet.Description).ToSymbolDisplayParts(),
                                glyph: Glyph.Snippet,
                                rules: rules);
            });
        }
    }
}
