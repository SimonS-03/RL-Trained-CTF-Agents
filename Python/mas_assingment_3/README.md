# Python Training

This folder contains the Python training scripts for the PacMan RL example.
For environment setup, use the UnityVecEnv documentation as the source of truth:

- [UnityVecEnv repository](https://github.com/martkartasev/UnityVecEnv)
- [UnityVecEnv setup and installation](https://github.com/martkartasev/UnityVecEnv/blob/master/README.md)
- [UnityVecEnv Python usage](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/python-usage.md)
- [UnityVecEnv Unity setup](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/unity-usage.md)

## Project-specific steps

1. Build the Unity executable for this project. The local build notes are in [../../Readme.MD](../../Readme.MD).
2. Set up UnityVecEnv using the git-based instructions from the docs above.
3. Update `unity_env_path` in `train_simple_catcher.py` so it points to your local `PacManCTF.exe`.
4. Run `python train_simple_catcher.py`.

`train_simple_catcher.py` launches the `PacManRLTrainExample` scene through `unity_env_factory.py`.
Training outputs are written under `runs/`, and the exported ONNX policy can be copied into the Unity project for inference.
Assuming your current working directory is this directory, you can check the tensorboard logs with. 
I have left a an example for you to explore. 

```
tensorboard --logdir ./runs
```
