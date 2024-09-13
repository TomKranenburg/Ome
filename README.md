(CAUTION: All code and documentation was written by Chat-GPT 4o)

<div align="center">
  <img src="ome-logo-large.png" alt="Logo">
</div>

# OME Documentation

## Overview

This application is a simple audio playback tool that dynamically loads `.flac` audio files from a specified directory and allows the user to control playback. Each audio track is represented by a label, a toggle button (to play/stop), and a volume slider. The application also supports loading and saving playback configurations, including window size, position, track play states, and volume settings.

## Features

1. **Dynamic Audio File Loading**: Automatically loads `.flac` files from the current directory or an "Audio" subdirectory.
2. **Playback Controls**: Each track has its own Play/Stop toggle button and a volume slider.
3. **Configuration Saving/Loading**: Users can save and load configurations for window settings, track volumes, and play states.
4. **Command-Line Arguments**: The application supports command-line arguments for configuration file loading.

## User Interface

### Main Window

The main window dynamically adjusts its height and width to fit the number of audio tracks loaded. It consists of the following elements for each audio track:
- **Label**: Displays the name of the audio file (without the extension).
- **Toggle Button**: Allows the user to play or stop the track. The button label toggles between "Play" and "Stop".
- **Volume Slider**: Adjusts the volume of the corresponding track, from 0 (mute) to 1 (maximum volume).

#### Window Behavior:
- **Minimum Window Height**: The window cannot be resized to a height smaller than 100 pixels.
- **Automatic Height Adjustment**: The window height dynamically adjusts based on the number of tracks loaded, but will not exceed the screen height.

## Command-Line Parameters

The application accepts up to three optional command-line parameters:

### 1. Configuration File Path
- **Usage**:

```
Ome.exe [ConfigFilePath]
```

- **Description**: Specifies the path to a configuration file (`.json`). When provided, the application will automatically load the saved window settings, track volumes, and playback states from the configuration file.
- **Example**:
  
```
Ome.exe C:\configs\audio_config.json
```

### 2. Sound Folder Path
- **Usage**:
```
Ome.exe [ConfigFilePath] [SoundFolderPath]
```
- **Description**: Specifies the directory containing `.flac` audio files. If not provided, the application defaults to the current working directory of the executable. If no `.flac` files are found in the current directory, it checks for an "Audio" folder in the current directory.
- **Example**:
```
Ome.exe C:\configs\audio_config.json C:\Users\Music\MyFlacFiles
```
- If both parameters are omitted, the application will use the current directory for audio files and will not load any pre-existing configuration.

### 3. Start Minimised
- **Usage**:
```
Ome.exe [ConfigFilePath] [SoundFolderPath] --minimized
```
- **Description**: This optional parameter starts the application minimized.
- **Example**:
```
Ome.exe C:\configs\audio_config.json C:\Users\Music\MyFlacFiles --minimized
```
## How the Program Functions

### 1. Loading Audio Files

- On startup, the program searches for `.flac` files in the current working directory of the executable.
- If no `.flac` files are found, the program looks for an `Audio` folder in the current directory.
- Each `.flac` file detected is added to the user interface with:
  - A **label** (filename without the extension).
  - A **toggle button** for Play/Stop functionality.
  - A **volume slider** to adjust the volume.

### 2. Track Playback

- **Play**: Pressing the "Play" button starts playing the track. The button text changes to "Stop".
- **Stop**: Pressing "Stop" stops the playback, and the button text changes back to "Play".
- **Volume**: Each track has an independent volume slider.

### 3. Configuration Management

- **Save Configuration**:
  - The user can save the current window size, position, track volumes, and playback states to a `.json` configuration file.
  - The saved file includes details about whether tracks are playing and their volume levels.
  
- **Load Configuration**:
  - On startup (or via the command-line parameter), the program can load a previously saved configuration.
  - It restores the window size and position, track volumes, and whether tracks are playing or stopped.
 
## Program Flow

1. **Application Startup**:
   - The program initializes and checks for command-line arguments.
   - It attempts to load a configuration file if one is provided.
   - The program then checks for `.flac` files in the current working directory or the "Audio" folder.

2. **UI Setup**:
   - The program dynamically creates UI elements for each audio file detected.
   - The window height and width adjust to fit the content.
   
3. **Track Interaction**:
   - Each track has independent play/stop controls and a volume slider.
   - Multiple tracks can be played simultaneously.

4. **Exiting the Program**:
   - When the program closes, it ensures all audio resources are properly disposed of.
   - If a configuration file path is provided, the current settings are saved automatically.

## Configuration File Structure

The configuration file is a `.json` file that stores the following data:

```json
{
  "Tracks": [
    {
      "FilePath": "C:/path/to/audio/file1.flac",
      "IsPlaying": true,
      "Volume": 0.8
    },
    {
      "FilePath": "C:/path/to/audio/file2.flac",
      "IsPlaying": false,
      "Volume": 0.5
    }
  ],
  "Window": {
    "Width": 800,
    "Height": 600,
    "Left": 100,
    "Top": 100
  }
}
```

1. Tracks: Contains an array of track objects with the following properties:
2. FilePath: The full path to the .flac file.
3. IsPlaying: A boolean indicating whether the track was playing when the configuration was saved.
4. Volume: The volume level of the track.
