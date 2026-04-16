import os
from dataclasses import dataclass

import tyro

from cleanrl_ppo_adversarial_mlp import Args as BaseArgs
from cleanrl_ppo_adversarial_mlp import run_ppo
from unity_env_factory import make_unity_env_factory


@dataclass
class Args(BaseArgs):
    exp_name: str = os.path.basename(__file__)[: -len(".py")]
    env_id: str = "PacManCTF"
    total_timesteps: int = 60000000
    hidden_size: int = 64
    num_envs: int = 1
    num_instances: int = 6
    unity_env_path: str = "/Users/ilian/dev/multiagent-1/A3_Group8/builds/macos_build.app/Contents/MacOS/PacManCTF"
    resume: str = ""
    auto_resume: bool = False
    
    # Adversarial-specific defaults
    role_obs_index: int = 0
    attacker_flag_threshold: float = 0.5
    include_role_flag_in_input: bool = False

    def __post_init__(self) -> None:
        if self.resume:
            self.resume_checkpoint = self.resume
        if self.auto_resume:
            self.auto_resume_latest = True


make_env = make_unity_env_factory(
    base_port=50010,
    scene_load="Training_2",
    physics_steps_per_action=5,
    log_file_prefix="train_adversarial_",  # Set to "" to disable logging files
)

if __name__ == "__main__":
    run_ppo(tyro.cli(Args), make_env)
