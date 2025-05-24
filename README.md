# What is this
This is a program that uses the Webserver feature of the Steam game [Nucleares](https://store.steampowered.com/app/1428420/Nucleares/) to steer the in-game reactor.

## Current features
Currently this program is able to steer the control rods and the condenser cooling pump (D1).

# How to set up
This is a program written in C#. There is no pre-compiled binary available, due to the fact that I do not know what OS you will be running this on. 

## Prerequisites
The game assumes that the game is running locally, and the game's Webserver must be started. You can start the Webserver by opening your tablet in-game, accessing the Status app and then clicking on "Start Webserver".

## Program setup
To run this, first download the code, which can be done either by cloning this repository, or by downloading the code as a ZIP: https://github.com/GHXX/NuclearesController/archive/refs/heads/master.zip (available also by clicking the green 'Code' button and clicking on 'Download ZIP').

Once you have downloaded the code you need to compile and execute it. 
For this, I recommend either using an IDE, such as Visual Studio Community 2022 (Windows only; VsCode is _not_ the same thing) or Jetbrains Rider. If you go this route, simply open up the .csproj file and then run the project. 
At this point the program should be running and working correctly (assuming you already had the .NET 8 SDK installed).

---

If you do not want to use an IDE or don't have one, you can simply download the .NET 8 SDK directly from microsoft.com: https://dotnet.microsoft.com/en-us/download/dotnet/8.0 (available for all platforms, including Linux and Mac).

After installing this SDK, open a console/terminal inside the directory that contains the .csproj file and then run the following command `dotnet run`. This compiles and runs the program.

Once you have executed this command, the program should run and be controlling your reactor (just like in the IDE case).

# Developing / Redistributing
This project is licensed under MIT, which is a very permissive license, even allowing selling derivative works, so feel free to use this code here to make your own controller. Though, please consider also making your projects open source, possibly also licensed as MIT - but this is not a requirement and does not in any way affect the MIT license terms.

If you are actually writing new code, I would recommend using an IDE to guide you a bit and give you proper autocompletion suggestions.