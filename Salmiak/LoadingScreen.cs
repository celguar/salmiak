using System;
using System.IO;
using Salmiak.Core.IO;
using Salmiak.Core.Formats;
using Salmiak.Rendering;

namespace Salmiak;

//A loading screen for the cold loads the editor cannot hide: opening the app streams a whole world
//map, and until the archives have handed over their tiles, their WMOs and their models the window
//only ever shows half a continent, which reads as the program hanging for a few seconds. So while
//that work is still waiting, the client's own loading screen stands in for the scene, drawn the way
//the client drew it: one picture stretched over the whole render area, with a progress bar over it
//that walks the work still left in the view. Maps that are a single building are not streamed at all
//and arrive in one go, so their hold is told by the clock instead and their bar walks the clock too.
//
//The picture is read out of the client's own tables and nothing else - the chain the client's loader
//walked: the map's row in Map.dbc, found by the Directory column that the map panel's list is built
//from, that row's loading screen id, that number looked up in the first column of LoadingScreens.dbc,
//and the FileName the row reached, a path straight at the picture. No picture is ever looked for by a
//name, since that is what gave a map another map's screen: the row's neighbours hold plenty of
//numbers, a dungeon id and an instance type among them, and one of them read as the screen id names a
//picture that belongs to whatever else shares that number. The fields are taken where the 1.12 client
//read them, at +4 for the directory and +0x98 for the id, because the client this editor runs is a
//modified 1.12.1 one and reads the tables at its loader's offsets. Where the chain names no file, or
//names one the archives do not hold, the client's own fallback picture is drawn - the one it created
//once as s_simpleBackgroundTexture at Interface\Glues\loading - and the status bar says which of the
//two answers was taken, so a map that came out wrong can be checked against the tables. The bar is
//put down in the two layers the client's own table holds - its track, the fill through it, the border
//over the fill, which is how the client layered them - and whatever the archives do not hold is drawn
//in the editor's own colours, so every cold load gets a moving bar.
public sealed class LoadingScreen
{
    //The bar as the client's own table stored it, in parts of the screen and with y from its bottom:
    //its frame is six tenths of the screen wide and a twentieth of it tall, and its fill sits inside
    //that frame rather than in the same box - five hundred twenty five thousandths wide and one
    //fortieth tall - so the frame's own edge keeps showing around the fill, which is what gives the
    //bar ends.
    private const float FrameX = 0.2f;
    private const float FrameY = 0.05f;
    private const float FrameW = 0.6f;
    private const float FrameH = 0.05f;
    private const float FillX = 0.2375f;
    private const float FillY = 0.0625f;
    private const float FillW = 0.525f;
    private const float FillH = 0.025f;

    //A bar this thin on a short window is a line rather than a bar, so its height is not let below
    //this many pixels.
    private const float BarMinH = 8f;

    //Where the 1.12 client read the map's directory inside a Map.dbc row, at +4. The map panel's list
    //is built from the same column, and so is the name of the folder the map's own files sit in, which
    //is why the name the user picked is this column's value rather than the display name beside it.
    private const int DirectoryField = 1;

    //Where the 1.12 client read the loading screen id inside a Map.dbc row, at +0x98 (its loader's
    //0x406e69 reads that dword and bounds it by the screen table's id bound).
    private const int MapScreenField = 38;

    //Where the client looked that number up - LoadingScreens.dbc's first column, which is what its id
    //lookup reads - and where it read the FileName of the row it reached, at +8 (its loader's 0x406e88).
    //The field beside it is the screen's display name, which the client never drew from.
    private const int ScreenPathField = 2;

    //The picture the client drew when its chain named no file: it created this one once, held as
    //s_simpleBackgroundTexture, so a load never shows only black.
    private const string ClientDefaultArt = @"Interface\Glues\loading";

    //The bar's two layers, as the paths the client's own table at 0x7ffd34 stored them: the fill, whose
    //right edge the progress moves, and the border drawn over it, which is what carries the track.
    private const string BarFillArt = @"Interface\Glues\LoadingBar\Loading-BarFill";
    private const string BarBorderArt = @"Interface\Glues\LoadingBar\Loading-BarBorder";

    //The same pair the sidebar bars carry, for a client that has no bar art of its own.
    private const float TrackR = 0.212f;
    private const float TrackG = 0.212f;
    private const float TrackB = 0.235f;
    private const float FillR = 0.180f;
    private const float FillG = 0.424f;
    private const float FillB = 0.769f;

    private readonly TextOverlay _paint;
    private readonly TextureCache _tex;
    private readonly MpqManager _mpq;

    //The ids belong to the texture cache, which frees them; this only holds handles.
    private int _bg, _barFrame, _barFill;
    private float _progress;
    private bool _active;

    //The picture that was drawn, so the status bar can say which one this map got and the user can
    //check it against the tables rather than guess.
    private string _artPath = "";

    //What the tables asked for, and whether it was not there: the fallback picture the client keeps is
    //otherwise indistinguishable from a map's own screen.
    private string _artAsked = "";
    private bool _askedMissing;
    private bool _usedFallback;

    public LoadingScreen(TextOverlay paint, TextureCache tex, MpqManager mpq)
    {
        _paint = paint;
        _tex = tex;
        _mpq = mpq;
    }

    public bool IsActive => _active;

    //Whether the picture was found, so the caller can say so when it was not: a cold load that drew
    //nothing but black would otherwise look like a load that finished.
    public bool ArtFound { get => _bg != 0; }

    //Which picture was drawn.
    public string ArtPath { get => _artPath; }

    //What the tables named for the map, and whether that file was missing from the archives, so a map
    //that came out with the wrong picture can be read off the status bar against its own row.
    public string ArtAsked { get => _artAsked; }
    public bool ArtMissing { get => _askedMissing; }

    //Whether the picture drawn is the client's own fallback rather than one the tables named for this
    //map, which is worth saying since the fallback looks like every map's screen.
    public bool ArtFallback { get => _usedFallback; }

    public void Enable(string mapName)
    {
        _progress = 0f;
        _active = true;
        _bg = _barFrame = _barFill = 0;
        _artPath = "";
        _artAsked = "";
        _askedMissing = false;
        _usedFallback = false;

        //The chain's answer, used as it stands: the picture and the row come out of the same tables read
        //the way the client read them, so they agree whatever the picture is called by - Badlands' own
        //picture is named for the crater inside it, and only the tables say that it is Badlands' screen.
        var chosen = ScreenFromDbc(mapName);
        _artAsked = chosen;
        _bg = LoadArt(chosen);

        //Where the chain named nothing, or named a picture this client's data does not carry, the
        //client's own fallback is what it drew, and the absence is remembered so that the fallback is
        //not read as the map's screen.
        if (_bg == 0)
        {
            _askedMissing = chosen.Length > 0;
            _bg = LoadArt(ClientDefaultArt);
            _usedFallback = _bg != 0;
        }

        _barFill = BarOf(BarFillArt);
        _barFrame = BarOf(BarBorderArt);
    }

    public void UpdateProgress(float ratio) => _progress = Math.Clamp(ratio, 0f, 1f);

    public void SetLoaded() => _progress = 1f;

    public void Disable()
    {
        _active = false;
        _progress = 0f;
    }

    public void Render(int viewportW, int viewportH)
    {
        if (!_active || viewportW <= 0 || viewportH <= 0) return;
        float vw = viewportW, vh = viewportH;

        //The picture covers the whole render area rather than keeping its own aspect: the client drew
        //its screens as one fullscreen quad and stretched them onto whatever window it was handed, so
        //a screen that came out square still fills the window the user is looking at, with no black
        //margins beside it.
        _paint.DrawRect(0f, 0f, vw, vh, 0f, 0f, 0f, 1f, viewportW, viewportH);
        if (_bg != 0) _paint.DrawImage(_bg, 0f, 0f, vw, vh, 1f, viewportW, viewportH);

        //The bar is put down back to front, which is how the client put its two quads down: its track,
        //then the fill through it, then the border over the fill. A border drawn before the fill lets
        //the fill bury it, which is what the eye reads as a bar running over its own ends.
        float frameX = FrameX * vw, frameY = vh * (1f - FrameY - FrameH);
        float frameW = FrameW * vw, frameH = Math.Max(BarMinH, FrameH * vh);

        //The track behind the fill is drawn only when the archives gave no border to draw, since the
        //border's own art is what carries the track on the clients that had it.
        if (_barFrame == 0)
            _paint.DrawRect(frameX, frameY, frameW, frameH, TrackR, TrackG, TrackB, 0.9f, viewportW, viewportH);

        //The fill is its own quad, a little narrower and thinner than the frame's, and it is drawn
        //from the left up to how far the work has got, which is what makes the bar read as work done
        //rather than as a picture being wiped away.
        float fillX = FillX * vw, fillY = vh * (1f - FillY - FillH);
        float fillW = FillW * vw, fillH = Math.Max(BarMinH * 0.6f, FillH * vh);

        if (_progress > 0.001f)
        {
            if (_barFill != 0)
                _paint.DrawImage(_barFill, fillX, fillY, fillW * _progress, fillH, 1f, viewportW, viewportH);
            else
                _paint.DrawRect(fillX, fillY, fillW * _progress, fillH, FillR, FillG, FillB, 1f,
                    viewportW, viewportH);
        }

        if (_barFrame != 0)
            _paint.DrawImage(_barFrame, frameX, frameY, frameW, frameH, 1f, viewportW, viewportH);
    }

    //The picture the client would have drawn for this map, read out of its own tables and nothing else:
    //the map's row in Map.dbc, that row's loading screen id, that number looked up in the first column
    //of LoadingScreens.dbc and the FileName the row reached, which is a path straight at the picture.
    //Nothing here looks a picture up by its name, because the names these files carry say nothing
    //about which map they belong to - Badlands keeps the picture of the crater inside it - and the row
    //holds plenty of other numbers that answer just as well for another map, which is what gave whole
    //sets of maps one screen each when the id column was looked for rather than read where the client
    //read it.
    private string ScreenFromDbc(string mapName)
    {
        var screens = LoadDbc("LoadingScreens");
        if (screens == null) return "";
        var maps = LoadDbc("Map");
        if (maps == null) return "";

        int row = MapRowFor(maps!, mapName);
        if (row < 0) return "";

        //The table has to be deep enough for the field the client read, and the number it holds has to
        //be one the screen table has a row for, which is the bound the client put on it too.
        if (FieldLimit(maps!) <= MapScreenField) return "";
        int rec = screens!.IndexOfId((uint)maps!.GetI(row, MapScreenField));
        if (rec < 0) return "";
        return ScreenPathOf(screens!, rec);
    }

    //The row of Map.dbc whose Directory column says the map the panel was asked for, matched exactly
    //apart from letter case. That column is the only name that says which row this is: it is what the
    //panel's list is built from and the folder the map's own files are filed under. The display name
    //beside it is not, and for the whole of Azeroth - id 0 - that name is Eastern Kingdoms, which is
    //another map's screen.
    private int MapRowFor(DbcFile maps, string mapName)
    {
        if (mapName.Length == 0 || FieldLimit(maps) <= DirectoryField) return -1;
        for (int i = 0; i < maps.RecordCount; i++)
            if (maps.GetString(i, DirectoryField).Equals(mapName, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    //The FileName the screen row reached, as the tables spell it: the field at 2, where the 1.12 client
    //read the path at +8. The fields either side are the row's id and the screen's display name, which
    //are not paths. A name the archives do not hold is still handed back, so the caller can say that
    //this client's data lacks the picture its own tables ask for rather than that the chain failed.
    private string ScreenPathOf(DbcFile screens, int rec)
    {
        if (rec < 0 || FieldLimit(screens) <= ScreenPathField) return "";
        var raw = screens.GetString(rec, ScreenPathField).Replace('/', '\\').Trim();
        return raw.Length == 0 ? "" : raw;
    }

    //Fields are read four bytes apart, which is how every reader of this table works, so a row shorter
    //than the header's field count cannot be read to its end.
    private static int FieldLimit(DbcFile dbc) => Math.Min(dbc.FieldCount, dbc.RecordSize / 4);

    private DbcFile? LoadDbc(string table)
    {
        var path = $"DBFilesClient\\{table}.dbc";
        if (!_mpq.FileExists(path)) return null;
        try
        {
            using var s = _mpq.OpenFile(path);
            return DbcFile.Read(s);
        }
        catch (Exception) { return null; }
    }

    //A path the tables or the client's own table gave, in the form the archives hold it: these paths are
    //kept without an ending, since the client hands them to its texture loader and the loader puts the
    //ending on, while the archives name their files with one. The name as it stands is asked for first,
    //for a table that kept the ending, and the name with .blp put on it second.
    private string ArtPathOf(string path)
    {
        if (path.Length == 0) return "";
        if (_mpq.FileExists(path)) return path;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
        {
            var with = path + ".blp";
            if (_mpq.FileExists(with)) return with;
        }
        return "";
    }

    //A picture named by the tables is decoded as it stands, at whatever size the client stored it, and
    //without any check on that size: it was named for the map being opened, so a client that keeps its
    //screens smaller than a window still gets its own picture rather than another map's.
    private int LoadArt(string path)
    {
        var real = ArtPathOf(path);
        if (real.Length == 0) return 0;
        byte[] rgba = new byte[0];
        int w = 0, h = 0;
        if (!ReadBlp(real, out rgba, out w, out h) || w <= 0) return 0;
        var id = _tex.GetOrUploadDecoded(real, rgba, w, h);
        if (id != 0) _artPath = real;
        return id;
    }

    //The bar layers only count when they came out of the archive readable, so a client that has none
    //leaves both ids at 0 and the drawn colours take their place.
    private int BarOf(string path)
    {
        var real = ArtPathOf(path);
        if (real.Length == 0) return 0;
        byte[] rgba = new byte[0];
        int w = 0, h = 0;
        if (!ReadBlp(real, out rgba, out w, out h) || w <= 0) return 0;
        return _tex.GetOrUploadDecoded(real, rgba, w, h);
    }

    //A failure leaves an empty picture rather than nothing, so nothing downstream has to ask which
    //of the two happened.
    private bool ReadBlp(string path, out byte[] rgba, out int w, out int h)
    {
        rgba = new byte[0];
        w = 0;
        h = 0;
        try
        {
            using var s = _mpq.OpenFile(path);
            var (d, dw, dh) = BlpReader.Decode(s);
            rgba = d;
            w = dw;
            h = dh;
            return true;
        }
        catch (Exception) { return false; }
    }
}
