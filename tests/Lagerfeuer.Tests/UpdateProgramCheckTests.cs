using System.Net;
using System.Security.Cryptography;
using Lagerfeuer.Services;

namespace Lagerfeuer.Tests;

/// <summary>
/// Der Updater nimmt nur an, was nach seinem Kopf ein Windows-Programm für diesen Rechner ist. Alles andere
/// verwirft er nach dem Download, bevor die laufende EXE angefasst oder etwas gestartet wird.
/// </summary>
public class UpdateProgramCheckTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Datei(byte[] inhalt, string name = "datei.exe")
    {
        var pfad = Path.Combine(_dir.Path, name);
        File.WriteAllBytes(pfad, inhalt);
        return pfad;
    }

    [Theory]
    // Portable EXE: Sie tritt an die Stelle der laufenden und muss deren Architektur haben.
    [InlineData(TestProgram.X64, true, "win-x64", true)]
    [InlineData(TestProgram.Arm64, true, "win-arm64", true)]
    [InlineData(TestProgram.Arm64, true, "win-x64", false)]
    [InlineData(TestProgram.X64, true, "win-arm64", false)]
    [InlineData(TestProgram.X86, true, "win-x64", false)]
    // Setup: NSIS baut es als 32-Bit-Programm, gleich für welche Architektur.
    [InlineData(TestProgram.X86, false, "win-x64", true)]
    [InlineData(TestProgram.X86, false, "win-arm64", true)]
    [InlineData(TestProgram.X64, false, "win-x64", true)]
    [InlineData(TestProgram.Arm64, false, "win-arm64", true)]
    // Andere Prozessorarten (hier ARM 32 Bit und Itanium) laufen auf keinem der beiden Ziele.
    [InlineData((ushort)0x01C4, false, "win-x64", false)]
    [InlineData((ushort)0x0200, true, "win-x64", false)]
    public void Prozessorart_MussZumRechnerPassen(ushort machine, bool portable, string rid, bool expected)
        => Assert.Equal(expected, UpdateService.IsWindowsProgram(Datei(TestProgram.Bytes(machine)), portable, rid));

    public static TheoryData<string, byte[]> KeinProgramm()
    {
        var ohnePe = TestProgram.Bytes();
        ohnePe[0x80] = (byte)'X'; // an der Stelle des PE-Kopfs steht etwas anderes

        var zeigtHinaus = TestProgram.Bytes();
        BitConverter.TryWriteBytes(zeigtHinaus.AsSpan(0x3C), zeigtHinaus.Length); // Verweis hinter das Dateiende

        var zeigtInDenDosKopf = TestProgram.Bytes();
        BitConverter.TryWriteBytes(zeigtInDenDosKopf.AsSpan(0x3C), 0);

        var negativ = TestProgram.Bytes();
        BitConverter.TryWriteBytes(negativ.AsSpan(0x3C), -1);

        return new()
        {
            { "Textdatei", "Das ist kein Programm, sondern ein Text, der nur so heißt.\r\n"u8.ToArray() },
            { "leere Datei", [] },
            { "nur MZ", "MZ"u8.ToArray() },
            { "DOS-Kopf ohne PE-Kopf dahinter", TestProgram.Bytes()[..64] },
            { "kein PE an der genannten Stelle", ohnePe },
            { "Verweis hinter das Dateiende", zeigtHinaus },
            { "Verweis in den DOS-Kopf", zeigtInDenDosKopf },
            { "negativer Verweis", negativ },
            { "PE-Kopf abgeschnitten", TestProgram.Bytes()[..(0x80 + 10)] },
            { "DLL statt EXE", TestProgram.Bytes(characteristics: TestProgram.Executable | TestProgram.Dll) },
            { "kein ausführbares Abbild", TestProgram.Bytes(characteristics: 0) },
            { "HTML-Seite", "<!DOCTYPE html><html><body>Not Found</body></html>"u8.ToArray() },
        };
    }

    [Theory]
    [MemberData(nameof(KeinProgramm))]
    public void WasKeinProgrammIst_WirdAbgelehnt(string fall, byte[] inhalt)
    {
        var pfad = Datei(inhalt);

        Assert.False(UpdateService.IsWindowsProgram(pfad, portable: false, "win-x64"), fall);
        Assert.False(UpdateService.IsWindowsProgram(pfad, portable: true, "win-x64"), fall);
    }

    [Fact]
    public void FehlendeDatei_GiltAlsNichtGeprueft()
        => Assert.False(UpdateService.IsWindowsProgram(Path.Combine(_dir.Path, "gibt-es-nicht.exe"), portable: false, "win-x64"));

    [Fact]
    public void PeKopfDarfWeitHintenLiegen()
        => Assert.True(UpdateService.IsWindowsProgram(Datei(TestProgram.Bytes(peOffset: 0x1000)), portable: false, "win-x64"));

    // ---------- im Ablauf des Downloads ----------

    private UpdateService Create(byte[] antwort, bool portable, string rid = "win-x64")
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(antwort) }));
        return new UpdateService(http, new Version(0, 1, 0), portable, rid, _dir.Path);
    }

    private static UpdateAsset Asset(string name, byte[] inhalt)
        => new(name, "https://example.test/" + name, inhalt.Length, Convert.ToHexString(SHA256.HashData(inhalt)));

    [Fact]
    public async Task Download_TextdateiMitPassenderPruefsumme_WirdVerworfen()
    {
        // So sähe ein Release aus, in dem unter dem richtigen Namen die falsche Datei liegt: GitHub bildet die
        // Prüfsumme aus dem, was hochgeladen wurde, sie stimmt also.
        var text = "Das ist kein Programm."u8.ToArray();
        var asset = Asset("Lagerfeuer-0.2.0-win-x64.exe", text);
        var service = Create(text, portable: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadAsync(asset, null, TestContext.Current.CancellationToken));

        Assert.Equal("Die heruntergeladene Datei ist kein Windows-Programm für diesen Rechner – Download verworfen.", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir.Path)); // nichts bleibt liegen, was jemand starten könnte
    }

    [Fact]
    public async Task Download_PortableExeFuerAndereArchitektur_WirdVerworfen()
    {
        var arm = TestProgram.Bytes(TestProgram.Arm64, "portabel");
        var service = Create(arm, portable: true, rid: "win-x64");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadAsync(Asset("Lagerfeuer-0.2.0-win-x64.exe", arm), null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Theory]
    [InlineData(true, TestProgram.X64, "Lagerfeuer-0.2.0-win-x64.exe")]
    [InlineData(false, TestProgram.X86, "Lagerfeuer-0.2.0-win-x64-setup.exe")]
    public async Task Download_Programm_WirdAngenommen(bool portable, ushort machine, string name)
    {
        var programm = TestProgram.Bytes(machine, "neue Fassung");
        var service = Create(programm, portable);

        var file = await service.DownloadAsync(Asset(name, programm), null, TestContext.Current.CancellationToken);

        Assert.Equal(programm, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
    }
}
