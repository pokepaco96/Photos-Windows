# Photos Windows – UI automation report

| | |
|---|---|
| Date (UTC) | 2026-11-08 10:15:24Z |
| OS | Microsoft Windows 10.0.26100 |
| Machine | runnervmm7ud1 |
| User interactive | True |
| Session id | 2 |
| Culture | en-US |
| Commit | 93551f9f8993255277623806ed519ac88015d2f8 |
| Executable | D:\a\Photos-Windows\Photos-Windows\artifacts\PhotosWindows-win-x64\PhotosWindows.exe |
| Work folder | D:\a\_temp\pwqa |
| Screen | 1920×1080 |
| Window | 1400×900 at 260,66 |
| Library counts at backup | 9 photos, 1 videos, 2 albums |
| Backup | PhotosWindows-Backup-Biblioteca QA-20261108-101150 |
| Library counts after restore | 9 photos, 1 videos, 2 albums |

**Passed:** 37 · **Failed:** 0 · **Blocked (not executed):** 0

| # | Scenario | Result | Time (s) | Evidence / error |
|---|---|---|---|---|
| 1 | First launch shows the welcome page | ✅ passed | 1.7 | [001_01_welcome.png](screenshots/001_01_welcome.png)<br>[002_01_welcome.txt](trees/002_01_welcome.txt) |
| 2 | Create a library (name dialog + real folder picker) | ✅ passed | 5 | [003_02_create_library_dialog.png](screenshots/003_02_create_library_dialog.png)<br>[004_02_empty_library.png](screenshots/004_02_empty_library.png) |
| 3 | Close and reopen the library from the welcome page | ✅ passed | 2.9 | [005_03_welcome_with_recent.png](screenshots/005_03_welcome_with_recent.png) |
| 4 | Import a folder with Copy to the library | ✅ passed | 12.1 | [006_import-options-dialog.png](screenshots/006_import-options-dialog.png)<br>[007_04_import_copy_done.png](screenshots/007_04_import_copy_done.png)<br>[008_04_library_with_photos.png](screenshots/008_04_library_with_photos.png) |
| 5 | Import a folder with Add without moving | ✅ passed | 10.2 | [009_import-options-dialog.png](screenshots/009_import-options-dialog.png)<br>[010_05_library_after_second_import.png](screenshots/010_05_library_after_second_import.png) |
| 6 | Duplicates are reported and not imported | ✅ passed | 10.9 | [011_06_import_report.png](screenshots/011_06_import_report.png)<br>[012_06_duplicates_page.png](screenshots/012_06_duplicates_page.png) |
| 7 | Corrupt, empty and disguised files are reported | ✅ passed | 9.8 | [013_07_unsupported_files.png](screenshots/013_07_unsupported_files.png) |
| 8 | Open a photo in the viewer | ✅ passed | 6.1 | [014_08_viewer_with_info.png](screenshots/014_08_viewer_with_info.png) |
| 9 | Navigate with Next/Previous and arrow keys | ✅ passed | 1.6 |  |
| 10 | Zoom in and out in the viewer | ✅ passed | 1.5 | [015_10_viewer_zoomed.png](screenshots/015_10_viewer_zoomed.png) |
| 11 | Open and play a video | ✅ passed | 10.2 | [016_11_video_tile.png](screenshots/016_11_video_tile.png)<br>[017_11_video_playing.png](screenshots/017_11_video_playing.png) |
| 12 | Create albums and folders; add and remove photos | ✅ passed | 14.6 | [018_12_albums_empty.png](screenshots/018_12_albums_empty.png)<br>[019_12_albums_page.png](screenshots/019_12_albums_page.png)<br>[020_12_album_content.png](screenshots/020_12_album_content.png) |
| 13 | Add and remove favorites | ✅ passed | 8.7 | [021_13_favorites.png](screenshots/021_13_favorites.png)<br>[022_13_favorites_empty.png](screenshots/022_13_favorites_empty.png) |
| 14 | Create a smart album (Favorite is true) | ✅ passed | 6 | [023_14_smart_album_dialog.png](screenshots/023_14_smart_album_dialog.png)<br>[024_14_smart_album.png](screenshots/024_14_smart_album.png) |
| 15 | Hide a photo and show it again | ✅ passed | 11.4 | [025_15_hidden.png](screenshots/025_15_hidden.png) |
| 16 | Search by keyword, year and type | ✅ passed | 7.2 | [026_16_search_beach.png](screenshots/026_16_search_beach.png)<br>[027_16_search_no_results.png](screenshots/027_16_search_no_results.png) |
| 17 | Edit a photo (exposure + Mono style) | ✅ passed | 9.2 | [028_17_editor_open.png](screenshots/028_17_editor_open.png)<br>[029_17_editor_mono_exposure.png](screenshots/029_17_editor_mono_exposure.png) |
| 18 | Undo and redo in the editor (buttons and Ctrl+Z / Ctrl+Y) | ✅ passed | 5.2 | [030_18_viewer_edited.png](screenshots/030_18_viewer_edited.png) |
| 19 | Restart the app: library reopens and edits persist; original unchanged | ✅ passed | 7.2 |  |
| 20 | Export the original (byte-identical) | ✅ passed | 7.7 | [031_20_export_dialog.png](screenshots/031_20_export_dialog.png) |
| 21 | Export the edited copy | ✅ passed | 6.8 |  |
| 22 | Create a verified backup | ✅ passed | 7.1 | [032_22_settings.png](screenshots/032_22_settings.png)<br>[033_22_backup_done.png](screenshots/033_22_backup_done.png) |
| 23 | Delete to Recently deleted, recover, and delete permanently | ✅ passed | 14.6 | [034_23_delete_confirmation.png](screenshots/034_23_delete_confirmation.png)<br>[035_23_recently_deleted.png](screenshots/035_23_recently_deleted.png)<br>[036_23_delete_permanently_plan.png](screenshots/036_23_delete_permanently_plan.png) |
| 24 | Switch light and dark theme | ✅ passed | 18.1 | [037_24_settings_dark.png](screenshots/037_24_settings_dark.png)<br>[038_24_library_dark.png](screenshots/038_24_library_dark.png)<br>[039_24_viewer_dark.png](screenshots/039_24_viewer_dark.png)<br>[040_24_settings_light.png](screenshots/040_24_settings_light.png)<br>[041_24_library_light.png](screenshots/041_24_library_light.png) |
| 25 | Keyboard navigation (arrows, Enter, Escape, Ctrl+F) | ✅ passed | 9.4 |  |
| 26 | Restore the backup as a new library | ✅ passed | 13 | [042_26_restored_library_settings.png](screenshots/042_26_restored_library_settings.png) |
| 27 | Visual pass of every page + accessible names | ✅ passed | 36.4 | [043_27_page_all.png](screenshots/043_27_page_all.png)<br>[044_27_page_videos.png](screenshots/044_27_page_videos.png)<br>[045_27_page_favorites.png](screenshots/045_27_page_favorites.png)<br>[046_27_page_recent.png](screenshots/046_27_page_recent.png)<br>[047_27_page_edited.png](screenshots/047_27_page_edited.png)<br>[048_27_page_located.png](screenshots/048_27_page_located.png)<br>[049_27_page_albums.png](screenshots/049_27_page_albums.png)<br>[050_27_page_duplicates.png](screenshots/050_27_page_duplicates.png)<br>[051_27_page_hidden.png](screenshots/051_27_page_hidden.png)<br>[052_27_page_trash.png](screenshots/052_27_page_trash.png)<br>[053_27_page_imports.png](screenshots/053_27_page_imports.png)<br>[054_27_page_settings.png](screenshots/054_27_page_settings.png)<br>[055_27_settings_tree.txt](trees/055_27_settings_tree.txt) |
| 28 | Originals unchanged (SHA-256) and clean exit | ✅ passed | 0.2 |  |
| 29 | People: download the face models after reading what is downloaded | ✅ passed | 14.7 | [056_29_settings_people_off.png](screenshots/056_29_settings_people_off.png)<br>[057_29_download_dialog.png](screenshots/057_29_download_dialog.png) |
| 30 | People: turn on for the library after the consent explanation | ✅ passed | 4.8 | [058_30_consent_dialog.png](screenshots/058_30_consent_dialog.png)<br>[059_30_settings_people_on.png](screenshots/059_30_settings_people_on.png) |
| 31 | People: analyse (cancel, then resume) and group the same people | ✅ passed | 23.1 | [060_import-options-dialog.png](screenshots/060_import-options-dialog.png)<br>[061_31_people_not_analysed.png](screenshots/061_31_people_not_analysed.png)<br>[062_31_people_grouped.png](screenshots/062_31_people_grouped.png) |
| 32 | People: name both groups | ✅ passed | 26 | [063_32_people_named.png](screenshots/063_32_people_named.png) |
| 33 | People: a person's photos and the viewer show the name | ✅ passed | 7.9 | [064_33_person_photos.png](screenshots/064_33_person_photos.png)<br>[065_33_viewer_people.png](screenshots/065_33_viewer_people.png) |
| 34 | People: split a face out (Not this person) and merge it back | ✅ passed | 10.8 | [066_34_faces_dialog.png](screenshots/066_34_faces_dialog.png)<br>[067_34_merge_dialog.png](screenshots/067_34_merge_dialog.png) |
| 35 | People: exclude a photo from People in the viewer, then include it again | ✅ passed | 19.8 | [068_35_excluded.png](screenshots/068_35_excluded.png) |
| 36 | People: names and groups persist after restarting the app | ✅ passed | 5.8 | [069_36_people_after_restart.png](screenshots/069_36_people_after_restart.png) |
| 37 | People: delete all face recognition data; the library stays intact | ✅ passed | 6.6 | [070_37_delete_all_confirmation.png](screenshots/070_37_delete_all_confirmation.png)<br>[071_37_library_after_delete.png](screenshots/071_37_library_after_delete.png) |
