namespace TypeToSquad.Core.Ports;

/// <summary>Plays synthesized audio through an output device.</summary>
public interface IAudioPlayer {
	/// <summary>Play raw PCM WAV data at a specific volume (0.0-1.0).</summary>
	void Play(byte[] wavData, float volumeMultiplier = 1.0f);

	/// <summary>Stop all currently playing audio.</summary>
	void StopAll();

	/// <summary>Get the list of available output device names.</summary>
	string[] GetOutputDevices();

	/// <summary>Get or set the current output device name. Empty = system default.</summary>
	string CurrentDevice { get; set; }
}
