using TypeToSquad.Core.Domain;

namespace TypeToSquad.Core.Ports;

/// <summary>Loads and saves <see cref="AppSettings"/> from persistent storage.</summary>
public interface ISettingsRepository {
	AppSettings Load();
	void Save(AppSettings settings);
}
