using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RelayRdp;

internal sealed class ProfileStore(string directory)
{
    private readonly string filePath = Path.Combine(directory, "profiles.json");
    private readonly DataContractJsonSerializer serializer = new(typeof(List<ConnectionProfile>));
    private readonly DataContractJsonSerializer profileSerializer = new(typeof(ConnectionProfile));

    public string Snapshot(ConnectionProfile profile)
    {
        using var stream = new MemoryStream();
        profileSerializer.WriteObject(stream, profile);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public List<ConnectionProfile> Load()
    {
        List<ConnectionProfile>? profiles;
        try
        {
            using var stream = File.OpenRead(filePath);
            profiles = (List<ConnectionProfile>?)serializer.ReadObject(stream);
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (DirectoryNotFoundException)
        {
            return new();
        }

        if (profiles is null || profiles.Any(profile => profile is null || profile.Id == Guid.Empty)
            || profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Count)
        {
            throw new InvalidDataException("Файл профилей повреждён.");
        }

        return profiles;
    }

    public List<ConnectionProfile> Update(Guid id, ConnectionProfile? replacement)
    {
        Directory.CreateDirectory(directory);
        using var storeLock = new FileStream(Path.Combine(directory, "profiles.lock"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var profiles = Load();
        var index = profiles.FindIndex(profile => profile.Id == id);
        if (replacement is null)
        {
            if (index < 0) return profiles;
            profiles.RemoveAt(index);
        }
        else if (index < 0)
        {
            profiles.Add(replacement);
        }
        else
        {
            profiles[index] = replacement;
        }

        var temporaryPath = Path.Combine(directory, $"profiles-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
                serializer.WriteObject(stream, profiles);
            if (File.Exists(filePath))
                File.Replace(temporaryPath, filePath, null);
            else
                File.Move(temporaryPath, filePath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return profiles;
    }
}
