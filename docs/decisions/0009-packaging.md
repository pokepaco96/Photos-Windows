# ADR-0009 · Empaquetado: app sin paquete y autocontenida

**Contexto.** Hay que producir un artefacto instalable y verificable en CI sin certificados de firma ni infraestructura de publicación.

**Decisión.** `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`: la app se ejecuta desde una carpeta (`PhotosWindows.exe`) sin instalar el runtime de Windows App SDK. La CI publica esa carpeta como artefacto ZIP para x64.

**Alternativas.** MSIX (instalación/desinstalación limpias e identidad de paquete, pero exige firmar con un certificado de confianza; se evaluará para la distribución pública), instalador MSI/Inno (más trabajo, sin ventajas para el MVP).

**Consecuencias.** Sin identidad de paquete: no se usan API que la requieran. Los selectores de archivos se inicializan con el HWND de la ventana. Los datos de usuario están fuera de la carpeta de la app, así que reinstalar o borrar la carpeta no afecta a las bibliotecas.
