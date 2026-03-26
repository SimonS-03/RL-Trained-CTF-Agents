import torch
import torch.nn as nn
import numpy as np
from mlagents_envs.environment import UnityEnvironment
from mlagents_envs.base_env import ActionTuple


class Network:
    def __init__(self):
        pass


def main():
    # If file_name == none then it looks for unity editor apparently, 
    # if we have a build we can put the exe file instead.
    env = UnityEnvironment(file_name=None, seed = 1, side_channels=[])
    env.reset()
    behaviour_name = list(env.behavior_specs.keys())[0] # Assuming we only have one "agent" type
    model = Network()
    
    print("Press play!")
    try:
        pass
    finally:
        env.close()

    
