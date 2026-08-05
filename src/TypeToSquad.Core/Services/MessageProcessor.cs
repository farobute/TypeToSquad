using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

using Rephidock.GeneralUtilities.Collections;

using TypeToSquad.Core.Domain;

using VoiceInfo = WinRTSpeechSynthServer.Protocol.VoiceInfo;

namespace TypeToSquad.Core.Services;

/// <summary>
/// Processes messages and allows use of some SSML features through markup.
/// Pure function — no Godot or UI dependencies.
/// </summary>
public static class MessageProcessor {

	// ================================================================
	// Public API
	// ================================================================

	/// <summary>
	/// Processes a raw message string into a RenderNode tree ready for synthesis.
	/// </summary>
	/// <param name="message">Raw text with optional markup tags.</param>
	/// <param name="textReplacements">Regex pattern→substitution rules, applied in order.</param>
	/// <param name="userTags">Custom tag (type, pattern, replacement) definitions.</param>
	/// <param name="voiceChanges">Hint→voiceKey mappings for [voice hint] tags.</param>
	/// <param name="maxReplacementPasses">Max passes of replacements before giving up.</param>
	/// <param name="defaultVoiceKey">Key of the default TTS voice.</param>
	/// <param name="getVoiceByKey">Lookup function: voice key → VoiceInfo.</param>
	/// <param name="onError">Optional error callback (replaces GD.PushError).</param>
	public static RenderNode ProcessMessage(
		string message,
		IReadOnlyList<(string pattern, string replacement)> textReplacements,
		IReadOnlyList<(string type, string pattern, string replacement)> userTags,
		IReadOnlyList<(string hint, string voiceKey)> voiceChanges,
		int maxReplacementPasses,
		string defaultVoiceKey,
		Func<string, VoiceInfo> getVoiceByKey,
		Action<string>? onError = null
	) {

		var segments = MessageLexer.SegmentMessage(message);

		// User tags and Text replacements (multi-pass)
		for (int i = 0, n = maxReplacementPasses; i < n; i++) {
			segments = PerformUserTagsPass(segments, userTags, out bool anyFound);
			segments = PerformReplacementPass(segments, textReplacements, out bool anyReplaced);
			if (!anyReplaced && !anyFound) break;

			if (i == n - 1) onError?.Invoke("Text replacement passes limit reached.");
		}

		// Compile
		var tree = SegmentsToInitialTree(segments, voiceChanges, defaultVoiceKey, getVoiceByKey);
		tree = ProcessInitialNodeTree(tree, onError);

		return tree;
	}

	/// <summary>
	/// Converts a RenderNode tree back to a string (text or SSML).
	/// Text nodes are rendered as text; every other node as a DOM element.
	/// </summary>
	public static string StringifyNodeRecursive(RenderNode root, bool indented = false) {

		StringBuilder sb = new();

		void AppendRecursiveHelper(RenderNode node, int indentLevel, bool isInsideDom) {

			string indentString = indented ? new string(' ', indentLevel * 4) : "";

			// Handle text nodes as text, not elements
			if (node.Type == RenderNodeType.Text) {
				if (indented) sb.Append(indentString);

				string textContent = node.Attributes[RenderNodeAttribute.TextContent];
				if (isInsideDom) textContent = SecurityElement.Escape(textContent);
				sb.Append(textContent);

				if (indented) sb.Append('\n');
				return;
			}

			sb.AppendJoin("", [indentString, "<", node.Type]);
			foreach (var pair in node.Attributes) {
				sb.AppendJoin<string>("", [" ", pair.Key, "=\"", pair.Value, "\""]);
			}
			sb.Append('>');
			if (indented) sb.Append('\n');

			foreach (var child in node.Children) {
				bool isChildInDom = isInsideDom || node.Type == RenderNodeType.SsmlRoot;
				AppendRecursiveHelper(child, indentLevel + 1, isChildInDom);
			}

			sb.AppendJoin("", [indentString, "</", node.Type, ">"]);
			if (indented) sb.Append('\n');
		}

		AppendRecursiveHelper(root, 0, root.Type == RenderNodeType.SsmlRoot);
		return sb.ToString();
	}

	// ================================================================
	// Text Replacements
	// ================================================================

	static string PerformReplacementsOnString(
		string text,
		IReadOnlyList<(string pattern, string replacement)> textReplacements
	) {
		string newText = text;

		foreach ((string pattern, string replacement) in textReplacements) {
			if (string.IsNullOrEmpty(pattern)) continue;

			Regex patternRegex = new Regex(pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
			newText = patternRegex.Replace(newText, replacement);

			bool hasReplaced = text != newText;
			if (hasReplaced) {
				bool newHasTags = newText.Contains(MessageLexer.TagOpen) || newText.Contains(MessageLexer.TagClose);
				if (newHasTags) break;
			}
		}

		return newText;
	}

	static List<MessageSegment> PerformReplacementPass(
		IEnumerable<MessageSegment> segments,
		IReadOnlyList<(string pattern, string replacement)> textReplacements,
		out bool anyTextReplaced
	) {
		List<MessageSegment> newSegments = new();
		anyTextReplaced = false;

		foreach (var seg in segments) {
			if (!seg.IsPlainText) {
				newSegments.Add(seg);
				continue;
			}

			string newText = PerformReplacementsOnString(seg.Text, textReplacements);

			if (newText != seg.Text) {
				anyTextReplaced = true;
				newSegments.AddRange(MessageLexer.SegmentMessage(newText));
				continue;
			}

			newSegments.Add(seg);
		}

		return newSegments;
	}

	// ================================================================
	// User Tags
	// ================================================================

	static string PerformTagRulesOnString(
		string tagType,
		string tagArgument,
		IReadOnlyList<(string type, string pattern, string replacement)> userTags
	) {
		string processedArg = tagArgument;

		foreach ((string type, string pattern, string replacement) in userTags) {
			if (type != tagType) continue;

			Regex patternRegex = new Regex(pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
			processedArg = patternRegex.Replace(processedArg, replacement);
		}

		return processedArg;
	}

	static List<MessageSegment> PerformUserTagsPass(
		List<MessageSegment> segments,
		IReadOnlyList<(string type, string pattern, string replacement)> userTags,
		out bool anyFound
	) {
		List<MessageSegment> newSegments = new();
		anyFound = false;

		// Gather user tag type names for quick lookup
		var userTagTypeNames = new HashSet<string>(userTags.Select(t => t.type));

		foreach (var seg in segments) {
			if (!seg.IsTag || !seg.IsValid) {
				newSegments.Add(seg);
				continue;
			}

			// Built-in tags pass through unchanged
			if (MessageLexer.BuiltInTagTypes.Contains(seg.TagType)) {
				newSegments.Add(seg);
				continue;
			}

			// Handle user tag
			if (userTagTypeNames.Contains(seg.TagType)) {
				string processedContent = PerformTagRulesOnString(seg.TagType, seg.TagArgument, userTags);
				newSegments.AddRange(MessageLexer.SegmentMessage(processedContent));
				anyFound = true;
			} else {
				// Unknown tag — pass through as-is
				newSegments.Add(seg);
			}
		}

		return newSegments;
	}

	// ================================================================
	// Tree Building
	// ================================================================

	static RenderNode SegmentsToInitialTree(
		IEnumerable<MessageSegment> segments,
		IReadOnlyList<(string hint, string voiceKey)> voiceChanges,
		string defaultVoiceKey,
		Func<string, VoiceInfo> getVoiceByKey
	) {
		var defaultVoice = getVoiceByKey(defaultVoiceKey);

		// Build voice change lookup
		var voiceChangeMap = voiceChanges.ToDictionary(vc => vc.hint, vc => vc.voiceKey);

		Stack<RenderNode> nodeStack = new();

		RenderNode root = CreateSsmlRoot(defaultVoice);
		nodeStack.Push(root);

		foreach (var seg in segments) {

			if (!seg.IsValid) continue;

			if (seg.IsPlainText) {
				AppendChildAtCurrent(CreateTextNode(seg.Text));
				continue;
			}

			switch (seg.TagType) {

				case MessageLexer.TagTypeEmpty: {

					RenderNode[] orderedParents = nodeStack.ToArray();
					int topVoiceIndex = -1;

					for (int i = 0; i < orderedParents.Length; i++) {
						if (orderedParents[i].Type == RenderNodeType.Voice) {
							topVoiceIndex = i;
							break;
						}
					}

					if (topVoiceIndex == -1) break;

					for (int i = 0; i <= topVoiceIndex; i++) {
						nodeStack.Pop();
					}
					for (int i = topVoiceIndex - 1; i >= 0; i--) {
						AppendChildAtCurrent(orderedParents[i].ShallowClone());
					}

				} break;

				case MessageLexer.TagTypeIpa:
					nodeStack.Peek().Children.Add(CreateIpaNode(seg.TagArgument));
					break;

				case MessageLexer.TagTypeVoice: {

					RenderNode[] orderedParents = nodeStack.ToArray();
					int topVoiceIndex = -1;
					for (int i = 0; i < orderedParents.Length; i++) {
						if (orderedParents[i].Type == RenderNodeType.Voice) {
							topVoiceIndex = i;
							break;
						}
					}

					if (topVoiceIndex != -1) {
						for (int i = 0; i <= topVoiceIndex; i++) {
							nodeStack.Pop();
						}
					}

					if (seg.TagArgument != "") {
						if (voiceChangeMap.TryGetValue(seg.TagArgument, out string? voiceKey)) {
							var voiceInfo = getVoiceByKey(voiceKey);
							var voiceNode = CreateVoiceNode(voiceInfo);
							AppendChildAtCurrent(voiceNode);
							nodeStack.Push(voiceNode);
						}
					}

					if (topVoiceIndex != -1) {
						for (int i = topVoiceIndex - 1; i >= 0; i--) {
							AppendChildAtCurrent(orderedParents[i].ShallowClone());
						}
					}

				} break;

				case MessageLexer.TagTypeBreak:
				case MessageLexer.TagTypeBreakAlt:
					AppendChildAtCurrent(CreateBreakNode(
						seg.TagArgument.Trim() == "" ? null : seg.TagArgument.Trim()
					));
					break;

				case MessageLexer.TagTypeAudio:
				case MessageLexer.TagTypeAudioAlt:
					AppendChildAtCurrent(CreateSoundNode(seg.TagArgument));
					break;

				default:
					// Unknown tags pass through as text
					AppendChildAtCurrent(CreateTextNode(seg.Text));
					break;
			}
		}

		return root;

		void AppendChildAtCurrent(RenderNode node) {
			nodeStack.Peek().Children.Add(node);
		}
	}

	// --- Node constructors ---

	static RenderNode CreateSsmlRoot(VoiceInfo defaultVoice) {
		return new RenderNode() {
			Type = RenderNodeType.SsmlRoot,
			Attributes = {
				{ RenderNodeAttribute.SsmlRootVersion, "1.0" },
				{ RenderNodeAttribute.SsmlXmlNamespace, "http://www.w3.org/2001/10/synthesis" },
				{ RenderNodeAttribute.SsmlLanguage, SecurityElement.Escape(defaultVoice.Language) },
			}
		};
	}

	static RenderNode CreateTextNode(string text) {
		return new RenderNode() {
			Type = RenderNodeType.Text,
			Attributes = { { RenderNodeAttribute.TextContent, text } }
		};
	}

	static RenderNode CreateVoiceNode(VoiceInfo voiceInfo) {
		return new RenderNode() {
			Type = RenderNodeType.Voice,
			Attributes = {
				{ RenderNodeAttribute.VoiceName, SecurityElement.Escape(voiceInfo.Name) },
				{ RenderNodeAttribute.VoiceLanguage, SecurityElement.Escape(voiceInfo.Language) },
			}
		};
	}

	static RenderNode CreateIpaNode(string phonemes) {
		return new RenderNode() {
			Type = RenderNodeType.Phoneme,
			Attributes = {
				{ RenderNodeAttribute.PhonemeAlphabet, "ipa" },
				{ RenderNodeAttribute.PhonemePhonemes, SecurityElement.Escape(phonemes) },
			}
		};
	}

	static RenderNode CreateBreakNode(string? time) {
		var node = new RenderNode() { Type = RenderNodeType.Break };
		if (time is not null) {
			node.Attributes.Add(RenderNodeAttribute.BreakTime, SecurityElement.Escape(time));
		}
		return node;
	}

	static RenderNode CreateSoundNode(string hint) {
		return new RenderNode() {
			Type = RenderNodeType.Sound,
			Attributes = { { RenderNodeAttribute.SoundHint, hint } }
		};
	}

	// ================================================================
	// Tree Normalization
	// ================================================================

	static RenderNode ProcessInitialNodeTree(RenderNode root, Action<string>? onError) {

		RenderNode serialRoot = new RenderNode() { Type = RenderNodeType.Serial };
		serialRoot.Children.Add(root);

		// 1: Pull out non-SSML tags via DFS
		void DfsPullOutWalk(RenderNode node, RenderNode? parent, int indexInParent, out bool pullOutCurrent) {

			if (node.Type == RenderNodeType.Sound) {
				pullOutCurrent = true;
				return;
			}

			if (node.Type == RenderNodeType.Break && !node.Attributes.ContainsKey(RenderNodeAttribute.BreakTime)) {
				pullOutCurrent = true;
				return;
			}

			for (int i = 0; i < node.Children.Count; i++) {

				DfsPullOutWalk(node.Children[i], node, i, out bool pullingOut);

				if (pullingOut && parent is not null) {
					var pullOutChild = node.Children[i];
					var followingChildren = node.Children[(i + 1)..];

					var nodeCopy = node.ShallowClone();
					node.Children.RemoveRange(i, node.Children.Count - i);
					nodeCopy.Children.AddRange(followingChildren);

					parent.Children.InsertRange(indexInParent + 1, [pullOutChild, nodeCopy]);

					if (i < node.Children.Count) {
						onError?.Invoke($"i < node.Children.Count assertion failed in {nameof(ProcessInitialNodeTree)}");
						break;
					}
				}
			}
			pullOutCurrent = false;
		}

		DfsPullOutWalk(serialRoot, null, -1, out _);

		// 2: Remove [break]s with no time attribute (they served their purpose)
		serialRoot.Children.RemoveAll(child =>
			child.Type == RenderNodeType.Break &&
			!child.Attributes.ContainsKey(RenderNodeAttribute.BreakTime)
		);

		// 3: If SSML only contains text, remove SSML wrapper
		for (int i = 0; i < serialRoot.Children.Count; i++) {
			RenderNode currentChild = serialRoot.Children[i];

			if (
				currentChild.Type == RenderNodeType.SsmlRoot &&
				currentChild.Children.All(node => node.Type == RenderNodeType.Text)
			) {
				RenderNode joinedTextNode =
					currentChild.Children.Count == 1
						? currentChild.Children[0]
						: CreateTextNode(
							currentChild
								.Children
								.Select(node => node.Attributes[RenderNodeAttribute.TextContent])
								.JoinString("")
						);

				serialRoot.Children.RemoveAt(i);
				serialRoot.Children.Insert(i, joinedTextNode);
			}
		}

		// 4: Remove empty text elements
		serialRoot.Children.RemoveAll(child =>
			child.Type == RenderNodeType.Text &&
			string.IsNullOrWhiteSpace(child.Attributes.GetValueOrDefault(RenderNodeAttribute.TextContent, ""))
		);

		// 5: Unwrap single-child serial
		if (serialRoot.Children.Count == 0) {
			onError?.Invoke($"0 children at the end of {nameof(ProcessInitialNodeTree)}.");
			return CreateTextNode("");
		}

		if (serialRoot.Children.Count == 1) {
			return serialRoot.Children[0];
		}

		return serialRoot;
	}
}
