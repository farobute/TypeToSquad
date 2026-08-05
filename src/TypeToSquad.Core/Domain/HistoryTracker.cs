using System;
using System.Collections.Generic;
using System.Linq;

namespace TypeToSquad.Core.Domain;

/// <summary>
/// In-memory history of submitted messages, most recent first.
/// Thread-safe for simple usage; navigation is intended for UI thread use.
/// </summary>
public class HistoryTracker {

	readonly LinkedList<string> history = new();

	/// <summary>Max number of entries retained. Negative values are treated as 0.</summary>
	public int HistorySlots { get; set; } = 32;

	/// <summary>Returns all stored entries, most recent first. Does not include the present entry.</summary>
	public string[] GetFullHistory() => history.ToArray();

	/// <summary>Adds an entry to the history.</summary>
	public void AddHistoryEntry(string text) {
		NavigateReset();
		history.AddFirst(text);
		EnforceHistoryCountMax();
	}

	/// <summary>Removes older entries, ensuring no more than <see cref="HistorySlots"/> are stored.</summary>
	public void EnforceHistoryCountMax() {
		int historySlots = Math.Max(0, HistorySlots);
		while (history.Count > historySlots) history.RemoveLast();

		if (currentHistoryNode is not null && currentHistoryNode.List is null) {
			NavigateReset();
		}
	}

	// --- Navigation ---

	LinkedListNode<string>? currentHistoryNode = null;

	/// <summary>The entry that would be added as most recent if history were not being navigated.</summary>
	string? presentEntry = null;

	/// <summary>Resets navigation to the present.</summary>
	public void NavigateReset() {
		currentHistoryNode = null;
		presentEntry = null;
	}

	/// <summary>Navigates further into the past. Returns true if successful.</summary>
	public bool TryNavigatePrevious(string currentText, out string queryResult) {

		if (currentHistoryNode == null && history.Count == 0) {
			queryResult = currentText;
			return false;
		}

		if (currentHistoryNode == null) {
			presentEntry = currentText;
			currentHistoryNode = history.First;
			queryResult = currentHistoryNode!.Value;
			return true;
		}

		if (currentHistoryNode == history.Last) {
			queryResult = currentText;
			return false;
		}

		currentHistoryNode = currentHistoryNode.Next;
		queryResult = currentHistoryNode!.Value;
		return true;
	}

	/// <summary>Navigates further towards the present. Returns true if successful.</summary>
	public bool TryNavigateNext(string currentText, out string queryResult) {

		if (currentHistoryNode == null) {
			queryResult = currentText;
			return false;
		}

		if (currentHistoryNode == history.First) {
			string present = presentEntry ?? "(null)";
			NavigateReset();
			queryResult = present;
			return true;
		}

		currentHistoryNode = currentHistoryNode.Previous;
		queryResult = currentHistoryNode!.Value;
		return true;
	}
}
