using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;

namespace DespliegueWarAutomatico;

class Program
{
    private static string _logFilePath = "";
    private static readonly object _logLock = new object();

    static int Main(string[] args)
    {
        try
        {
            string rootSetup = ConfigurationManager.AppSettings["rootSetup"];
            string nameSetup = ConfigurationManager.AppSettings["nameSetup"];

            if (string.IsNullOrEmpty(rootSetup) || string.IsNullOrEmpty(nameSetup))
            {
                EscribirLog("[ERROR] Faltan 'rootSetup' o 'nameSetup' en el app.config.", true);
                return 1;
            }

            string setupFilePath = Path.Combine(rootSetup, nameSetup);
            if (!File.Exists(setupFilePath))
            {
                EscribirLog($"[ERROR] No se encontró el archivo de configuración: {setupFilePath}", true);
                return 1;
            }

            var config = ParsearSetupFile(setupFilePath);

            // Lectura de configuraciones
            string rutaOrigenBase = ObtenerValor(config, "RutaOrigenBase", "DEPLOY_SETTINGS");
            string nombreArchivo = ObtenerValor(config, "NombreArchivo", "DEPLOY_SETTINGS");
            string rutaRemota = ObtenerValor(config, "RutaRemota", "DEPLOY_SETTINGS");
            string nombreFinalDestino = ObtenerValor(config, "NombreFinalDestino", "DEPLOY_SETTINGS") ?? "AEROMEXICO.war";

            string logPath = ObtenerValor(config, "LOG_PATH", "LogPath");
            string logFileName = ObtenerValor(config, "LOG_FILE_NAME", "LogPath");

            _logFilePath = string.IsNullOrEmpty(logPath) || string.IsNullOrEmpty(logFileName)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Despliegue.log")
                : Path.Combine(logPath, logFileName);

            string logDir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            EscribirLog("==================================================");
            EscribirLog($"Iniciando proceso de despliegue de archivo WAR");
            EscribirLog($"Hora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // Validación de rutas requeridas
            if (string.IsNullOrEmpty(rutaOrigenBase) || string.IsNullOrEmpty(nombreArchivo) || string.IsNullOrEmpty(rutaRemota))
            {
                EscribirLog("[ERROR] Faltan parámetros requeridos (RutaOrigenBase, NombreArchivo o RutaRemota) en la configuración.", true);
                return 1;
            }

            string archivoOrigenPath = Path.Combine(rutaOrigenBase, nombreArchivo);
            if (!File.Exists(archivoOrigenPath))
            {
                EscribirLog($"[ERROR] No existe el archivo de origen: {archivoOrigenPath}", true);
                return 1;
            }

            if (!Directory.Exists(rutaRemota))
            {
                EscribirLog($"[ERROR] No se puede acceder a la ruta remota: {rutaRemota}", true);
                return 1;
            }

            string archivoDestinoPath = Path.Combine(rutaRemota, nombreFinalDestino);

            // 1. Comparación de fecha de modificación
            if (File.Exists(archivoDestinoPath))
            {
                DateTime fechaOrigen = File.GetLastWriteTime(archivoOrigenPath);
                DateTime fechaDestino = File.GetLastWriteTime(archivoDestinoPath);

                if (fechaOrigen <= fechaDestino)
                {
                    EscribirLog($"[INFO] El archivo en origen no es más reciente que el remoto. Proceso omitido.");
                    EscribirLog($"   - Modificación Origen:  {fechaOrigen:yyyy-MM-dd HH:mm:ss}");
                    EscribirLog($"   - Modificación Remoto:  {fechaDestino:yyyy-MM-dd HH:mm:ss}");
                    return 0;
                }
            }

            EscribirLog($"Archivo nuevo detectado en origen: {nombreArchivo}");

            // 2. Renombrado/Respaldo con número correlativo del archivo existente en la ruta remota
            if (File.Exists(archivoDestinoPath))
            {
                string archivoRespaldoPath = GenerarNombreRespaldoCorrelativo(rutaRemota, nombreFinalDestino);
                File.Move(archivoDestinoPath, archivoRespaldoPath);
                EscribirLog($"[BACKUP] Archivo remoto actual renombrado a: {Path.GetFileName(archivoRespaldoPath)}");
            }

            // 3. Copia del nuevo archivo desde origen a la ruta remota renombrándolo al nombre principal
            EscribirLog($"Copiando {nombreArchivo} -> {archivoDestinoPath}...");
            File.Copy(archivoOrigenPath, archivoDestinoPath, true);
            EscribirLog("[ÉXITO] Despliegue de archivo WAR completado correctamente.");

            return 0;
        }
        catch (Exception ex)
        {
            EscribirLog($"[EXCEPCIÓN] Error no controlado durante el despliegue: {ex.Message}\n{ex.StackTrace}", true);
            return 2;
        }
        finally
        {
            EscribirLog($"Fin del proceso - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            EscribirLog("==================================================");
        }
    }

    // Calcula el siguiente número correlativo (ejemplo: AEROMEXICO.war_1, AEROMEXICO.war_2)
    private static string GenerarNombreRespaldoCorrelativo(string carpeta, string nombreBase)
    {
        int maxNumero = 0;

        var archivosExistentes = Directory.GetFiles(carpeta, $"{nombreBase}_*");
        foreach (var archivo in archivosExistentes)
        {
            string nombre = Path.GetFileName(archivo);
            string extensionNumerica = nombre.Substring($"{nombreBase}_".Length);

            if (int.TryParse(extensionNumerica, out int numeroActual))
            {
                if (numeroActual > maxNumero)
                    maxNumero = numeroActual;
            }
        }

        int siguienteNumero = maxNumero + 1;
        return Path.Combine(carpeta, $"{nombreBase}_{siguienteNumero}");
    }

    public static void EscribirLog(string mensaje, bool esError = false)
    {
        lock (_logLock)
        {
            string linea = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {mensaje}";
            Console.ForegroundColor = esError ? ConsoleColor.Red : ConsoleColor.White;
            Console.WriteLine(linea);
            Console.ResetColor();

            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, linea + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    private static Dictionary<string, string> ParsearSetupFile(string filePath)
    {
        var dict = new Dictionary<string, string>();
        string currentSection = "";

        foreach (var line in File.ReadAllLines(filePath))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                currentSection = trimmed.TrimStart('[').TrimEnd(']');
                continue;
            }

            if (trimmed.Contains('|'))
            {
                var parts = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    string key = parts[0];
                    string value = parts[1];
                    string fullKey = string.IsNullOrEmpty(currentSection) ? key : $"{currentSection}.{key}";
                    dict[fullKey] = value;
                }
            }
        }
        return dict;
    }

    private static string ObtenerValor(Dictionary<string, string> dict, string key, string section = null)
    {
        string fullKey = string.IsNullOrEmpty(section) ? key : $"{section}.{key}";
        return dict.TryGetValue(fullKey, out string value) ? value : null;
    }
}