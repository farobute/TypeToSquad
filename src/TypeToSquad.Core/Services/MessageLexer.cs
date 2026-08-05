using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TypeToSquad.Core.Services;

/// <summary>
/// Splits a message into <see cref="Domain.MessageSegment"/>s,
/// where each segment is either plain text or a tag.
/// </summary>
public static class MessageLexer {

	public const char TagOpen = '[';
	public const char TagClose = ']';

	// --- Built-in tag types ---

	public const string TagTypeEmpty = "";
	public const string TagTypeIpa = "ipa";
	public const string TagTypeVoice = "voice";
	public const string TagTypeAudio = "audio";
	public const string TagTypeAudioAlt = "sound";
	public const string TagTypeBreak = "break";
	public const string TagTypeBreakAlt = "wait";

	public static readonly ReadOnlyCollection<string> BuiltInTagTypes = new[] {
		TagTypeEmpty,
		TagTypeIpa,
		TagTypeVoice,
		TagTypeAudio,
		TagTypeAudioAlt,
		TagTypeBreak,
		TagTypeBreakAlt,
	}.AsReadOnly();

	/// <summary>Returns true if the tag type is built-in or in the provided user tag types.</summary>
	public static bool IsTagTypeValid(string tagType, IEnumerable<string> userTagTypes) {
		if (BuiltInTagTypes.Contains(tagType)) return true;
		return userTagTypes.Contains(tagType);
	}

	/// <summary>Returns true if the tag type is a "running change" (voice or empty reset).</summary>
	public static bool IsTagRunningChange(string tagType) {
		return tagType == TagTypeVoice || tagType == TagTypeEmpty;
	}

	// --- Lexing ---

	/// <summary>Returns a list of segments that make up the message.</summary>
	/// <remarks>The segments' <see cref="Domain.MessageSegment.Text"/>s add up perfectly to the initial string.</remarks>
	public static List<Domain.MessageSegment> SegmentMessage(string message) {

		List<Domain.MessageSegment> segments = new();

		int currentSegmentStartI = 0;

		for (int i = 0; i < message.Length; i++) {

			// Scan until tag opening is found
			if (message[i] != TagOpen) continue;

			// Add text before tag (if there is any between tags)
			if (i != currentSegmentStartI) {
				segments.Add(MakePlainSegment(message[currentSegmentStartI..i]));
				currentSegmentStartI = i;
			}

			// Find closing tag (assuming nesting)
			int additionalDepth = 0;
			bool hasNested = false;

			i++; // "consume" tag opening
			for (/*[nop]*/; i < message.Length; i++) {

				if (message[i] == TagOpen) {
					additionalDepth++;
					hasNested = true;
					continue;
				}

				if (message[i] == TagClose) {
					if (additionalDepth == 0) break;
					additionalDepth--;
					continue;
				}

				/*[continue]*/
			}

			// here: `i` is at ']' or == message.Length

			// Unclosed tag
			if (i >= message.Length) {
				segments.Add(MakeInvalidSegment(message[currentSegmentStartI..]));
				currentSegmentStartI = message.Length;
				break;
			}

			// Closed tag but has nesting
			if (hasNested) {
				segments.Add(MakeInvalidSegment(message[currentSegmentStartI..(i + 1)]));
				currentSegmentStartI = i + 1;
				continue;
			}

			// Valid tag
			segments.Add(MakeTagSegment(message[currentSegmentStartI..(i + 1)]));
			currentSegmentStartI = i + 1;

			// [continue]
		}

		// Add till the end
		if (currentSegmentStartI < message.Length) {
			segments.Add(MakePlainSegment(message[currentSegmentStartI..]));
		}

		return segments;
	}

	// --- Constructing Segments ---

	public static Domain.MessageSegment MakePlainSegment(string str) {
		return new Domain.MessageSegment {
			IsValid = true,
			Text = str
		};
	}

	public static Domain.MessageSegment MakeInvalidSegment(string str) {
		return new Domain.MessageSegment {
			IsValid = false,
			Text = str
		};
	}

	public static Domain.MessageSegment MakeTagSegment(string strWithBrackets) {

		(string type, string argument) = ParseTag(strWithBrackets, out _);

		return new Domain.MessageSegment {
			IsValid = true,
			Text = strWithBrackets,
			IsTag = true,
			TagType = type,
			TagArgument = argument
		};
	}

	public static (string type, string argument) ParseTag(string tagWithBrackets, out int? argumentStartIndex) {

		int typeStartIndex = -1;
		int typeExclusiveEndIndex = -1;

		for (int i = 1; i < tagWithBrackets.Length - 1; i++) {
			if (char.IsWhiteSpace(tagWithBrackets[i])) continue;
			typeStartIndex = i;
			break;
		}

		if (typeStartIndex < 0) {
			// Empty tag
			argumentStartIndex = null;
			return ("", "");
		}

		for (int i = typeStartIndex; i < tagWithBrackets.Length - 1; i++) {
			if (!char.IsWhiteSpace(tagWithBrackets[i])) continue;
			typeExclusiveEndIndex = i;
			break;
		}

		if (typeExclusiveEndIndex < 0) {
			// Empty argument
			argumentStartIndex = null;
			return (tagWithBrackets[typeStartIndex..^1], "");
		}

		argumentStartIndex = typeExclusiveEndIndex + 1;

		if (argumentStartIndex >= tagWithBrackets.Length - 1) {
			// Empty argument (but with space separator)
			argumentStartIndex = null;
			return (tagWithBrackets[typeStartIndex..typeExclusiveEndIndex], "");
		}

		return (
			tagWithBrackets[typeStartIndex..typeExclusiveEndIndex],
			tagWithBrackets[(typeExclusiveEndIndex + 1)..^1]
		);
	}

	// --- Convenience ---

	/// <summary>Returns a new list of segments where adjacent plain text segments are joined into one.</summary>
	public static List<Domain.MessageSegment> CombineAdjacentPlainTextSegments(List<Domain.MessageSegment> segments) {

		List<Domain.MessageSegment> newSegments = new();

		foreach (var seg in segments) {

			// Add non-plain-text
			if (!seg.IsPlainText) {
				newSegments.Add(seg);
				continue;
			}

			// Add plain-text after non-plain-text
			if (newSegments.Count == 0 || !newSegments[^1].IsPlainText) {
				newSegments.Add(seg);
				continue;
			}

			// Join text segments
			newSegments[^1] = MakePlainSegment(newSegments[^1].Text + seg.Text);
		}

		return newSegments;
	}
}
