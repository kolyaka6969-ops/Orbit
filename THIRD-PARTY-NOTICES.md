# Сторонние компоненты

Orbit поставляется вместе со следующими библиотеками. Их лицензии сохраняются.

| Компонент | Для чего | Лицензия |
| --- | --- | --- |
| [.NET](https://github.com/dotnet/runtime) (рантайм, WPF, Windows Forms) | основа приложения | MIT |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) и [ImageMagick](https://imagemagick.org) (с libraw, libheif, libwebp, libaom и др.) | изображения и RAW | Apache 2.0 / ImageMagick License; лицензии встроенных библиотек — в пакете Magick.NET |
| [PDFsharp](https://github.com/empira/PDFsharp) | объединение и разделение PDF | MIT |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | распаковка 7z, RAR, TAR, GZ, BZ2, XZ | MIT |

Аудио, видео, отрисовка страниц PDF и распознавание текста выполняются компонентами самой Windows
(Media Foundation, Windows.Data.Pdf, Windows.Media.Ocr).

Идея кругового меню, которое появляется при перетаскивании файла и нажатии Shift / Ctrl / Alt,
взята из проекта [Sweetch](https://github.com/krllmrzv/Sweetch) (MIT License, © 2026 Metamorfolog, © 2026 mmmorozov).
Код Orbit написан заново.
