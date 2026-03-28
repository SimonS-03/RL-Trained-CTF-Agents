import os
from dataclasses import dataclass

import tyro

from cleanrl_ppo import Args as BaseArgs
from cleanrl_ppo import run_ppo
from unity_env_factory import make_unity_env_factory


@dataclass
class Args(BaseArgs):
    exp_name: str = os.path.basename(__file__)[: -len(".py")]
    env_id: str = "Catcher"
    total_timesteps: int = 10000000
    hidden_size: int = 64
    num_envs: int = 300
    unity_env_path: str = "/home/ilian/multiagents/A3_Group8/builds/linux_build/linux_build.x86_64"


make_env = make_unity_env_factory(
    base_port=50030,
    scene_load="PacManRLTrainExample",
    physics_steps_per_action=5,
    log_file_prefix="",  # Set to "" to disable logging files
)

if __name__ == "__main__":
    run_ppo(tyro.cli(Args), make_env)
