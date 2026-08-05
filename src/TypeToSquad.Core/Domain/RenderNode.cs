using System;
using System.Collections.Generic;

namespace TypeToSquad.Core.Domain;

/// <summary>
/// Represents a node of a tree that makes up the message after it has been parsed and processed.
/// The tree can be the root.
/// </summary>
public record RenderNode {

	public RenderNodeType Type { get; set; } = RenderNodeType.Text;

	public List<RenderNode> Children { get; } = new();

	public Dictionary<RenderNodeAttribute, string> Attributes { get; } = new();

	/// <summary>
	/// Creates a copy of this node and its attributes but not children.
	/// </summary>
	public RenderNode ShallowClone() {
		RenderNode other = new() { Type = this.Type };
		foreach (var pair in this.Attributes) other.Attributes.Add(pair.Key, pair.Value);
		return other;
	}
}

public sealed class RenderNodeType(string value) : IEquatable<RenderNodeType> {

	public static readonly RenderNodeType
		Text = new("text"),
		SsmlRoot = new("speak"),
		Voice = new("voice"),
		Phoneme = new("phoneme"),
		Break = new("break"),
		Sound = new("sound"),
		Serial = new("serial");

	readonly string value = value;

	public override string ToString() => value;
	public override int GetHashCode() => value.GetHashCode();

	public bool Equals(RenderNodeType? other) {
		if (other is null) return false;
		if (ReferenceEquals(this, other)) return true;
		return value == other.value;
	}

	public override bool Equals(object? obj) =>
		ReferenceEquals(this, obj) || obj is RenderNodeType other && Equals(other);

	public static bool operator ==(RenderNodeType? left, RenderNodeType? right) => Equals(left, right);
	public static bool operator !=(RenderNodeType? left, RenderNodeType? right) => !Equals(left, right);
	public static implicit operator string(RenderNodeType obj) => obj.ToString();
}

public sealed class RenderNodeAttribute(string value) : IEquatable<RenderNodeAttribute> {

	public static readonly RenderNodeAttribute
		TextContent = new("content"),
		SsmlRootVersion = new("version"),
		SsmlXmlNamespace = new("xmlns"),
		SsmlLanguage = new("xml:lang"),
		VoiceName = new("name"),
		VoiceLanguage = new("xml:lang"),
		PhonemeAlphabet = new("alphabet"),
		PhonemePhonemes = new("ph"),
		BreakTime = new("time"),
		SoundHint = new("hint");

	readonly string value = value;

	public override string ToString() => value;
	public override int GetHashCode() => value.GetHashCode();

	public bool Equals(RenderNodeAttribute? other) {
		if (other is null) return false;
		if (ReferenceEquals(this, other)) return true;
		return value == other.value;
	}

	public override bool Equals(object? obj) =>
		ReferenceEquals(this, obj) || obj is RenderNodeAttribute other && Equals(other);

	public static bool operator ==(RenderNodeAttribute? left, RenderNodeAttribute? right) => Equals(left, right);
	public static bool operator !=(RenderNodeAttribute? left, RenderNodeAttribute? right) => !Equals(left, right);
	public static implicit operator string(RenderNodeAttribute obj) => obj.ToString();
}
