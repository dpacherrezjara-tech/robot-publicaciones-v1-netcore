using CertificadoAutomatico;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
//SISTEMA PARA CERTIFICADOS NORMAL XAMP - CASI TODO CLIENTES
namespace CertificadoAutomaticoXampp;

class Program
{
    private static string _logFilePath = "";
    private static readonly object _logLock = new object();

    static int Main(string[] args)
    {
        bool exitoGeneracion = false;
        bool exitoDeploy = false;
        string errorGeneracion = "";
        string errorDeploy = "";
        string certFinal = "";

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
            string sourcePath = ObtenerValor(config, "SOURCE_PATH", "CERTIFICATE_SETTINGS");
            string outputPath = ObtenerValor(config, "OUTPUT_PATH", "CERTIFICATE_SETTINGS") ?? sourcePath;

            string logPath = ObtenerValor(config, "LOG_PATH", "LogPath");
            string logFileName = ObtenerValor(config, "LOG_FILE_NAME", "LogPath");

            // Limpieza de rutas de log
            logPath = logPath?.Trim(' ', '"');
            logFileName = logFileName?.Trim(' ', '"', '\\', '/');

            _logFilePath = string.IsNullOrEmpty(logPath) || string.IsNullOrEmpty(logFileName)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Certificado.log")
                : Path.Combine(logPath, logFileName);

            string logDir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            EscribirLog("==================================================");
            EscribirLog("Iniciando proceso de preparación de certificados SSL para XAMPP Apache");
            EscribirLog($"Hora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            if (string.IsNullOrEmpty(sourcePath))
            {
                EscribirLog("[ERROR] No se encontró SOURCE_PATH en [CERTIFICATE_SETTINGS]", true);
                return 1;
            }

            sourcePath = LimpiarRuta(sourcePath);
            outputPath = LimpiarRuta(outputPath);

            EscribirLog($"Origen de certificados: {sourcePath}");
            EscribirLog($"Destino de salida: {outputPath}");

            // Identificar dinámicamente el archivo .crt principal en origen
            string archivoOrigenPath = Directory.GetFiles(sourcePath, "*.crt")
                                               .FirstOrDefault(f => !Path.GetFileName(f).Equals("ca_bundle.crt", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(archivoOrigenPath))
            {
                EscribirLog($"[ERROR] No se encontró un archivo .crt válido en: {sourcePath}", true);
                return 1;
            }

            string nombreCertificado = Path.GetFileName(archivoOrigenPath);
            certFinal = Path.Combine(outputPath, nombreCertificado);

            // 1. COMPARACIÓN DE FECHA DE MODIFICACIÓN
            if (File.Exists(archivoOrigenPath) && File.Exists(certFinal) && !archivoOrigenPath.Equals(certFinal, StringComparison.OrdinalIgnoreCase))
            {
                DateTime fechaOrigen = File.GetLastWriteTime(archivoOrigenPath);
                DateTime fechaDestino = File.GetLastWriteTime(certFinal);

                if (fechaOrigen <= fechaDestino)
                {
                    EscribirLog("[INFO] El certificado de origen no es más reciente que el actual. Proceso omitido.");
                    EscribirLog($"   - Modificación Origen:  {fechaOrigen:yyyy-MM-dd HH:mm:ss}");
                    EscribirLog($"   - Modificación Destino: {fechaDestino:yyyy-MM-dd HH:mm:ss}");
                    return 0;
                }
            }

            EscribirLog("Nuevo certificado detectado en origen.");

            // 2. GENERACIÓN / CONCATENACIÓN DE CERTIFICADOS CON SUS NOMBRES ORIGINALES
            exitoGeneracion = PrepararArchivosCertificado(sourcePath, outputPath, out errorGeneracion);

            if (!exitoGeneracion)
            {
                EscribirLog($"[ERROR] Falló la preparación de certificados: {errorGeneracion}", true);
            }

            // 3. DESPLIEGUE DIRECTO A APACHE Y BACKUP EXCLUSIVO EN BACKUP_SETTINGS
            if (exitoGeneracion)
            {
                string enableDeploy = ObtenerValor(config, "ENABLE_DEPLOY", "DEPLOY_SETTINGS");
                if (enableDeploy?.ToLower() == "true")
                {
                    string apachePath = ObtenerValor(config, "APACHE_PATH", "DEPLOY_SETTINGS");
                    string apacheService = ObtenerValor(config, "APACHE_SERVICE_NAME", "DEPLOY_SETTINGS") ?? "Apache2.4";

                    string enableBackup = ObtenerValor(config, "ENABLE_BACKUP", "BACKUP_SETTINGS");
                    string backupDestination = ObtenerValor(config, "BACKUP_DESTINATION", "BACKUP_SETTINGS");
                    string backupSourceFolders = ObtenerValor(config, "BACKUP_SOURCE_FOLDERS", "BACKUP_SETTINGS") ?? sourcePath;

                    if (string.IsNullOrEmpty(apachePath))
                    {
                        EscribirLog("[ERROR] DEPLOY activado pero falta APACHE_PATH en la configuración.", true);
                        exitoDeploy = false;
                        errorDeploy = "No se especificó APACHE_PATH";
                    }
                    else
                    {
                        apachePath = LimpiarRuta(apachePath);

                        EscribirLog("Iniciando despliegue a Apache XAMPP...");
                        exitoDeploy = DesplegarEnApache(outputPath, apachePath, apacheService, out errorDeploy);

                        if (exitoDeploy)
                        {
                            EscribirLog("[ÉXITO] Despliegue en Apache XAMPP completado.");

                            // ÚNICO LUGAR DONDE SE HACEN BACKUPS Y ARCHIVADO DE FUENTE
                            if (enableBackup?.ToLower() == "true")
                            {
                                ProcesarBackupYArchivarFuentes(backupSourceFolders, backupDestination);
                            }
                        }
                        else
                        {
                            EscribirLog($"[ERROR] Falló el despliegue en Apache: {errorDeploy}", true);
                        }
                    }
                }
                else
                {
                    EscribirLog("Despliegue a Apache desactivado (ENABLE_DEPLOY != true).");
                    exitoDeploy = true;
                }
            }

            // 4. NOTIFICACIÓN POR EMAIL
            string enableEmail = ObtenerValor(config, "ENABLE_EMAIL", "EMAIL_SETTINGS");
            if (enableEmail?.ToLower() == "true")
            {
                EscribirLog("[EMAIL] Enviando notificación por correo...");

                string apachePathCfg = ObtenerValor(config, "APACHE_PATH", "DEPLOY_SETTINGS");
                string certPathParaEmail = certFinal;

                if (exitoDeploy && !string.IsNullOrEmpty(apachePathCfg))
                {
                    string apacheBase = LimpiarRuta(apachePathCfg).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (apacheBase.EndsWith(@"\conf", StringComparison.OrdinalIgnoreCase))
                    {
                        apacheBase = Directory.GetParent(apacheBase)?.FullName ?? apacheBase;
                    }

                    string serverCrtApache = Path.Combine(apacheBase, "conf", "ssl.crt", "server.crt");
                    if (File.Exists(serverCrtApache))
                    {
                        certPathParaEmail = serverCrtApache;
                    }
                }

                string expiryDate = ObtenerFechaExpiracion(certPathParaEmail);
                string serverName = ObtenerValor(config, "ENV_NAME", "ENVIRONMENT_SETTINGS") ?? Environment.MachineName;

                string estado;
                string colorEstado;

                bool huboError = !exitoGeneracion || !exitoDeploy;

                if (huboError)
                {
                    estado = "ERROR en el proceso";
                    colorEstado = "#e53e3e";

                    string detallesError = "";
                    if (!exitoGeneracion)
                        detallesError = $"Error en preparación: {errorGeneracion}";
                    else if (!exitoDeploy)
                        detallesError = $"Error en despliegue: {errorDeploy}";

                    estado = $"{estado} - {detallesError}";
                }
                else
                {
                    estado = "Actualización exitosa";
                    colorEstado = "#38a169";
                }

                var emailService = new EmailService(config);
                bool exitoEmail = emailService.SendSuccessNotification(
                    expiryDate,
                    serverName,
                    estado,
                    colorEstado,
                    out string errorEmail
                );

                if (exitoEmail)
                    EscribirLog("[EMAIL] Notificación enviada correctamente.");
                else
                    EscribirLog($"[EMAIL] Falló el envío del correo: {errorEmail}", true);
            }
            else
            {
                EscribirLog("[EMAIL] Notificaciones por correo desactivadas (ENABLE_EMAIL != true).");
            }

            if (exitoGeneracion && exitoDeploy)
            {
                EscribirLog("[ÉXITO] Proceso completado correctamente.");
                return 0;
            }
            else
            {
                EscribirLog("[ERROR] El proceso finalizó con errores.", true);
                return 1;
            }
        }
        catch (Exception ex)
        {
            EscribirLog($"[EXCEPCIÓN] Error no controlado durante el proceso: {ex.Message}\n{ex.StackTrace}", true);
            return 2;
        }
        finally
        {
            EscribirLog($"Fin del proceso - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            EscribirLog("==================================================");
        }
    }

    static bool PrepararArchivosCertificado(string sourcePath, string outputPath, out string errorMsg)
    {
        errorMsg = "";

        string certFile = Directory.GetFiles(sourcePath, "*.crt")
                                   .FirstOrDefault(f => !Path.GetFileName(f).Equals("ca_bundle.crt", StringComparison.OrdinalIgnoreCase));
        string caFile = Path.Combine(sourcePath, "ca_bundle.crt");
        string keyFile = Directory.GetFiles(sourcePath, "*.key").FirstOrDefault();

        if (string.IsNullOrEmpty(certFile) || string.IsNullOrEmpty(keyFile) || !File.Exists(caFile))
        {
            errorMsg = $"Faltan archivos de origen en: {sourcePath} (requeridos: un archivo .crt, ca_bundle.crt y un archivo .key)";
            return false;
        }

        string nombreCert = Path.GetFileName(certFile);
        string nombreKey = Path.GetFileName(keyFile);

        string certDestino = Path.Combine(outputPath, nombreCert);
        string keyDestino = Path.Combine(outputPath, nombreKey);

        try
        {
            if (!Directory.Exists(outputPath))
                Directory.CreateDirectory(outputPath);

            EscribirLog($"Generando {nombreCert} ({nombreCert} + ca_bundle.crt)...");
            string contenidoCert = File.ReadAllText(certFile);
            string contenidoCa = File.ReadAllText(caFile);

            if (!contenidoCert.EndsWith("\n") && !contenidoCert.EndsWith("\r"))
                contenidoCert += Environment.NewLine;

            File.WriteAllText(certDestino, contenidoCert + contenidoCa);
            EscribirLog($"Archivo generado: {certDestino}");

            if (!sourcePath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            {
                EscribirLog($"Copiando {nombreKey} -> {keyDestino}...");
                File.Copy(keyFile, keyDestino, true);
                EscribirLog($"Archivo generado: {keyDestino}");
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMsg = $"Error preparando certificados: {ex.Message}";
            return false;
        }
    }

    static bool DesplegarEnApache(string origenDir, string apacheDir, string servicioApache, out string errorMsg)
    {
        errorMsg = "";

        string origenCrt = Directory.GetFiles(origenDir, "*.crt")
                                    .FirstOrDefault(f => !Path.GetFileName(f).Equals("ca_bundle.crt", StringComparison.OrdinalIgnoreCase));
        string origenKey = Directory.GetFiles(origenDir, "*.key").FirstOrDefault();

        if (string.IsNullOrEmpty(origenCrt) || string.IsNullOrEmpty(origenKey))
        {
            errorMsg = $"No se encontraron archivos .crt o .key en {origenDir}";
            return false;
        }

        string apacheBase = apacheDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (apacheBase.EndsWith(@"\conf", StringComparison.OrdinalIgnoreCase))
        {
            apacheBase = Directory.GetParent(apacheBase)?.FullName ?? apacheBase;
        }

        string destinoSslCrtDir = Path.Combine(apacheBase, "conf", "ssl.crt");
        string destinoSslKeyDir = Path.Combine(apacheBase, "conf", "ssl.key");

        try
        {
            if (!Directory.Exists(destinoSslCrtDir)) Directory.CreateDirectory(destinoSslCrtDir);
            if (!Directory.Exists(destinoSslKeyDir)) Directory.CreateDirectory(destinoSslKeyDir);

            // Nombres estándar requeridos por la configuración predeterminada de XAMPP (httpd-ssl.conf)
            string destinoCrt = Path.Combine(destinoSslCrtDir, "server.crt");
            string destinoKey = Path.Combine(destinoSslKeyDir, "server.key");

            // Detener servicio Apache
            bool usoServicio = DetenerOIniciarServicio(servicioApache, detener: true);

            // Reemplazo directo con nombres normalizados (server.crt y server.key)
            EscribirLog($"Actualizando en Apache: {origenCrt} -> {destinoCrt}");
            File.Copy(origenCrt, destinoCrt, true);

            EscribirLog($"Actualizando en Apache: {origenKey} -> {destinoKey}");
            File.Copy(origenKey, destinoKey, true);

            // Reiniciar Apache
            if (usoServicio)
            {
                DetenerOIniciarServicio(servicioApache, detener: false);
            }
            else
            {
                string httpdPath = Path.Combine(apacheBase, "bin", "httpd.exe");
                if (File.Exists(httpdPath))
                {
                    EscribirLog($"Reiniciando Apache mediante ejecutable directo: {httpdPath}");
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = httpdPath,
                        Arguments = "-k restart",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit();
                }
                else
                {
                    EscribirLog("[ADVERTENCIA] No se encontró httpd.exe para reiniciar Apache automáticamente.", true);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMsg = $"Error en el despliegue a Apache: {ex.Message}";
            return false;
        }
    }

    // 🔧 HÍBRIDO: copia al backup con timestamp Y MUEVE el original a 'procesados/'
    //           (antes se llamaba ProcesarBackupYVaciarFuentes y hacía File.Delete)
    private static void ProcesarBackupYArchivarFuentes(string backupSourceFolders, string backupDestination)
    {
        if (string.IsNullOrEmpty(backupDestination))
        {
            EscribirLog("[ADVERTENCIA] BACKUP activado pero no se especificó BACKUP_DESTINATION.", true);
            return;
        }

        backupDestination = LimpiarRuta(backupDestination);
        if (!Directory.Exists(backupDestination))
            Directory.CreateDirectory(backupDestination);

        string[] fuentes = backupSourceFolders.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        foreach (var fuenteRaw in fuentes)
        {
            string fuente = LimpiarRuta(fuenteRaw);

            if (!Directory.Exists(fuente))
            {
                EscribirLog($"[ADVERTENCIA] La carpeta fuente de backup no existe: {fuente}");
                continue;
            }

            // 🔧 HÍBRIDO: subcarpeta 'procesados' dentro de cada fuente
            string carpetaProcesados = Path.Combine(fuente, "procesados");
            if (!Directory.Exists(carpetaProcesados))
            {
                Directory.CreateDirectory(carpetaProcesados);
                EscribirLog($"[HÍBRIDO] Carpeta de procesados creada: {carpetaProcesados}");
            }

            var archivos = Directory.GetFiles(fuente);
            foreach (var archivo in archivos)
            {
                try
                {
                    string nombreArchivo = Path.GetFileNameWithoutExtension(archivo);
                    string extension = Path.GetExtension(archivo);

                    // 1. Copiar al backup con timestamp (igual que antes)
                    string nombreNuevo = $"{nombreArchivo}_{timeStamp}{extension}";
                    string destinoPath = Path.Combine(backupDestination, nombreNuevo);
                    File.Copy(archivo, destinoPath, true);

                    // 2. Mover el original a 'procesados/' (antes: File.Delete)
                    string destinoProcesado = Path.Combine(carpetaProcesados, Path.GetFileName(archivo));
                    File.Move(archivo, destinoProcesado, true);

                    EscribirLog($"[BACKUP + ARCHIVADO] {Path.GetFileName(archivo)} -> backup: {nombreNuevo} | procesados: {Path.GetFileName(destinoProcesado)}");
                }
                catch (Exception ex)
                {
                    EscribirLog($"[ERROR BACKUP] No se pudo procesar el archivo {archivo}: {ex.Message}", true);
                }
            }
        }
    }

    static bool DetenerOIniciarServicio(string nombreServicio, bool detener)
    {
        try
        {
            using var sc = new ServiceController(nombreServicio);
            if (detener)
            {
                if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                {
                    EscribirLog($"Deteniendo servicio: {nombreServicio}...");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            else
            {
                if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
                {
                    EscribirLog($"Iniciando servicio: {nombreServicio}...");
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            EscribirLog($"[ADVERTENCIA] No se pudo manipular el servicio '{nombreServicio}': {ex.Message}", true);
            return false;
        }
    }

    static string ObtenerFechaExpiracion(string certPath)
    {
        if (!File.Exists(certPath))
            return "No disponible (archivo no encontrado)";

        try
        {
            using var cert = new X509Certificate2(certPath);
            return cert.NotAfter.ToString("dd/MM/yyyy HH:mm:ss");
        }
        catch
        {
            try
            {
                string contenido = File.ReadAllText(certPath);
                int inicio = contenido.IndexOf("-----BEGIN CERTIFICATE-----");
                int fin = contenido.IndexOf("-----END CERTIFICATE-----");

                if (inicio != -1 && fin != -1)
                {
                    string pemCert = contenido.Substring(inicio, (fin + "-----END CERTIFICATE-----".Length) - inicio);
                    using var cert = X509Certificate2.CreateFromPem(pemCert);
                    return cert.NotAfter.ToString("dd/MM/yyyy HH:mm:ss");
                }

                return "No disponible (formato no reconocido)";
            }
            catch (Exception ex)
            {
                EscribirLog($"[ADVERTENCIA] No se pudo leer la fecha de expiración del certificado: {ex.Message}", true);
                return "No disponible (error al leer)";
            }
        }
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

    private static string LimpiarRuta(string ruta)
    {
        if (string.IsNullOrEmpty(ruta)) return "";
        ruta = ruta.Trim(' ', '"');
        if (!ruta.EndsWith(Path.DirectorySeparatorChar.ToString()))
            ruta += Path.DirectorySeparatorChar;
        return ruta;
    }
}