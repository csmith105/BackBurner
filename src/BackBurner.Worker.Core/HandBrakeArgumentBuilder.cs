using System.Globalization;
using BackBurner.Contracts;

namespace BackBurner.Worker.Core;

public static class HandBrakeArgumentBuilder
{
    public static IReadOnlyList<string> Build(string source, string partialDestination, HandBrakeSettings settings)
    {
        var arguments = new List<string>
        {
            "--json",
            "-i", source,
            "-o", partialDestination,
            "-f", settings.Container == "mkv" ? "av_mkv" : "av_mp4",
            "-e", settings.VideoEncoder,
            "-q", settings.Quality.ToString(CultureInfo.InvariantCulture),
            "--encoder-preset", settings.EncoderPreset
        };
        if (settings.MaxWidth is not null)
        {
            arguments.AddRange(["-w", settings.MaxWidth.Value.ToString(CultureInfo.InvariantCulture)]);
        }
        if (settings.MaxHeight is not null)
        {
            arguments.AddRange(["-l", settings.MaxHeight.Value.ToString(CultureInfo.InvariantCulture)]);
        }
        if (settings.AllAudio)
        {
            arguments.Add("--all-audio");
        }
        arguments.AddRange(["-E", settings.AudioEncoder]);
        if (settings.AudioBitrateKbps is not null)
        {
            arguments.AddRange(["-B", settings.AudioBitrateKbps.Value.ToString(CultureInfo.InvariantCulture)]);
        }
        if (settings.AllSubtitles)
        {
            arguments.Add("--all-subtitles");
        }
        if (settings.IncludeChapterMarkers)
        {
            arguments.Add("--markers");
        }
        arguments.AddRange(settings.ExtraArguments);
        return arguments;
    }
}
