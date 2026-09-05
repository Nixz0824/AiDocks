using System.Runtime.InteropServices;
using System.Text;

namespace TwinDock.Native;

internal static class WinSqlite
{
    public const int OpenReadOnly = 0x00000001;
    public const int OpenUri = 0x00000040;
    public const int Row = 100;
    private static readonly nint SqliteTransient = new(-1);

    public static string? QueryText(string databasePath, string sql, string parameter)
    {
        var utf8Path = Utf8Z(ToUri(databasePath));
        var status = Open(utf8Path, out var database, OpenReadOnly | OpenUri, 0);
        if (status != 0)
        {
            if (database != 0)
            {
                Close(database);
            }

            return null;
        }

        try
        {
            if (Prepare(database, Utf8Z(sql), -1, out var statement, 0) != 0)
            {
                return null;
            }

            try
            {
                BindText(statement, 1, Utf8Z(parameter), -1, SqliteTransient);
                if (Step(statement) != Row)
                {
                    return null;
                }

                var pointer = ColumnText(statement, 0);
                return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
            }
            finally
            {
                Finalize(statement);
            }
        }
        finally
        {
            Close(database);
        }
    }

    private static string ToUri(string path)
    {
        var normalized = path.Replace('\\', '/');
        return $"file:{normalized}?mode=ro";
    }

    private static byte[] Utf8Z(string value) => Encoding.UTF8.GetBytes(value + "\0");

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
    private static extern int Open(byte[] filename, out nint database, int flags, nint vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close")]
    private static extern int Close(nint database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
    private static extern int Prepare(nint database, byte[] sql, int byteCount, out nint statement, nint tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_text")]
    private static extern int BindText(nint statement, int index, byte[] value, int byteCount, nint destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
    private static extern int Step(nint statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
    private static extern nint ColumnText(nint statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
    private static extern int Finalize(nint statement);
}
