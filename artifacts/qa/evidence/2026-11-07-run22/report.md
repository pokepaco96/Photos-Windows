# Photos Windows – UI automation report

| | |
|---|---|
| Date (UTC) | 2026-11-07 18:54:32Z |
| OS | Microsoft Windows 10.0.26100 |
| Machine | runnervmm7ud1 |
| User interactive | True |
| Session id | 2 |
| Culture | en-US |
| Commit | f2f3315cc55ea45c5806908e828af232a1eebbac |
| Executable | D:\a\Photos-Windows\Photos-Windows\artifacts\PhotosWindows-win-x64\PhotosWindows.exe |
| Work folder | D:\a\_temp\pwqa |
| Screen | 1920×1080 |
| Window | 1400×900 at 260,66 |
| Library counts at backup | 9 photos, 1 videos, 2 albums |
| Backup | PhotosWindows-Backup-Biblioteca QA-20261107-185258 |
| Library counts after restore | 9 photos, 1 videos, 2 albums |

**Passed:** 28 · **Failed:** 0 · **Blocked (not executed):** 0

| # | Scenario | Result | Time (s) | Evidence / error |
|---|---|---|---|---|
| 1 | First launch shows the welcome page | ✅ passed | 2 | [001_01_welcome.png](screenshots/001_01_welcome.png)<br>[002_01_welcome.txt](trees/002_01_welcome.txt) |
| 2 | Create a library (name dialog + real folder picker) | ✅ passed | 5 | [003_02_create_library_dialog.png](screenshots/003_02_create_library_dialog.png)<br>[004_02_empty_library.png](screenshots/004_02_empty_library.png) |
| 3 | Close and reopen the library from the welcome page | ✅ passed | 3 | [005_03_welcome_with_recent.png](screenshots/005_03_welcome_with_recent.png) |
| 4 | Import a folder with Copy to the library | ✅ passed | 12.1 | [006_import-options-dialog.png](screenshots/006_import-options-dialog.png)<br>[007_04_import_copy_done.png](screenshots/007_04_import_copy_done.png)<br>[008_04_library_with_photos.png](screenshots/008_04_library_with_photos.png) |
| 5 | Import a folder with Add without moving | ✅ passed | 9.8 | [009_import-options-dialog.png](screenshots/009_import-options-dialog.png)<br>[010_05_library_after_second_import.png](screenshots/010_05_library_after_second_import.png) |
| 6 | Duplicates are reported and not imported | ✅ passed | 11 | [011_06_import_report.png](screenshots/011_06_import_report.png)<br>[012_06_duplicates_page.png](screenshots/012_06_duplicates_page.png) |
| 7 | Corrupt, empty and disguised files are reported | ✅ passed | 10.2 | [013_07_unsupported_files.png](screenshots/013_07_unsupported_files.png) |
| 8 | Open a photo in the viewer | ✅ passed | 6.8 | [014_08_viewer_with_info.png](screenshots/014_08_viewer_with_info.png) |
| 9 | Navigate with Next/Previous and arrow keys | ✅ passed | 1.9 |  |
| 10 | Zoom in and out in the viewer | ✅ passed | 1.4 | [015_10_viewer_zoomed.png](screenshots/015_10_viewer_zoomed.png) |
| 11 | Open and play a video | ✅ passed | 9.7 | [016_11_video_tile.png](screenshots/016_11_video_tile.png)<br>[017_11_video_playing.png](screenshots/017_11_video_playing.png) |
| 12 | Create albums and folders; add and remove photos | ✅ passed | 14.8 | [018_12_albums_empty.png](screenshots/018_12_albums_empty.png)<br>[019_12_albums_page.png](screenshots/019_12_albums_page.png)<br>[020_12_album_content.png](screenshots/020_12_album_content.png) |
| 13 | Add and remove favorites | ✅ passed | 9 | [021_13_favorites.png](screenshots/021_13_favorites.png)<br>[022_13_favorites_empty.png](screenshots/022_13_favorites_empty.png) |
| 14 | Create a smart album (Favorite is true) | ✅ passed | 6 | [023_14_smart_album_dialog.png](screenshots/023_14_smart_album_dialog.png)<br>[024_14_smart_album.png](screenshots/024_14_smart_album.png) |
| 15 | Hide a photo and show it again | ✅ passed | 11.8 | [025_15_hidden.png](screenshots/025_15_hidden.png) |
| 16 | Search by keyword, year and type | ✅ passed | 7.4 | [026_16_search_beach.png](screenshots/026_16_search_beach.png)<br>[027_16_search_no_results.png](screenshots/027_16_search_no_results.png) |
| 17 | Edit a photo (exposure + Mono style) | ✅ passed | 9.3 | [028_17_editor_open.png](screenshots/028_17_editor_open.png)<br>[029_17_editor_mono_exposure.png](screenshots/029_17_editor_mono_exposure.png) |
| 18 | Undo and redo in the editor (buttons and Ctrl+Z / Ctrl+Y) | ✅ passed | 5.4 | [030_18_viewer_edited.png](screenshots/030_18_viewer_edited.png) |
| 19 | Restart the app: library reopens and edits persist; original unchanged | ✅ passed | 7.7 |  |
| 20 | Export the original (byte-identical) | ✅ passed | 7.8 | [031_20_export_dialog.png](screenshots/031_20_export_dialog.png) |
| 21 | Export the edited copy | ✅ passed | 7.1 |  |
| 22 | Create a verified backup | ✅ passed | 7 | [032_22_settings.png](screenshots/032_22_settings.png)<br>[033_22_backup_done.png](screenshots/033_22_backup_done.png) |
| 23 | Delete to Recently deleted, recover, and delete permanently | ✅ passed | 14.7 | [034_23_delete_confirmation.png](screenshots/034_23_delete_confirmation.png)<br>[035_23_recently_deleted.png](screenshots/035_23_recently_deleted.png)<br>[036_23_delete_permanently_plan.png](screenshots/036_23_delete_permanently_plan.png) |
| 24 | Switch light and dark theme | ✅ passed | 18.1 | [037_24_settings_dark.png](screenshots/037_24_settings_dark.png)<br>[038_24_library_dark.png](screenshots/038_24_library_dark.png)<br>[039_24_viewer_dark.png](screenshots/039_24_viewer_dark.png)<br>[040_24_settings_light.png](screenshots/040_24_settings_light.png)<br>[041_24_library_light.png](screenshots/041_24_library_light.png) |
| 25 | Keyboard navigation (arrows, Enter, Escape, Ctrl+F) | ✅ passed | 9.1 |  |
| 26 | Restore the backup as a new library | ✅ passed | 12.2 | [042_26_restored_library_settings.png](screenshots/042_26_restored_library_settings.png) |
| 27 | Visual pass of every page + accessible names | ✅ passed | 36.6 | [043_27_page_all.png](screenshots/043_27_page_all.png)<br>[044_27_page_videos.png](screenshots/044_27_page_videos.png)<br>[045_27_page_favorites.png](screenshots/045_27_page_favorites.png)<br>[046_27_page_recent.png](screenshots/046_27_page_recent.png)<br>[047_27_page_edited.png](screenshots/047_27_page_edited.png)<br>[048_27_page_located.png](screenshots/048_27_page_located.png)<br>[049_27_page_albums.png](screenshots/049_27_page_albums.png)<br>[050_27_page_duplicates.png](screenshots/050_27_page_duplicates.png)<br>[051_27_page_hidden.png](screenshots/051_27_page_hidden.png)<br>[052_27_page_trash.png](screenshots/052_27_page_trash.png)<br>[053_27_page_imports.png](screenshots/053_27_page_imports.png)<br>[054_27_page_settings.png](screenshots/054_27_page_settings.png)<br>[055_27_settings_tree.txt](trees/055_27_settings_tree.txt) |
| 28 | Originals unchanged (SHA-256) and clean exit | ✅ passed | 0.6 |  |
