import os
from dataclasses import dataclass
from typing import Any, Callable

import numpy as np
import torch
import torch.nn as nn
from torch.distributions import Normal

from cleanrl_ppo import layer_init
from cleanrl_ppo import run_ppo as base_run_ppo


@dataclass
class Args:
    exp_name: str = os.path.basename(__file__)[: -len(".py")]
    """the name of this experiment"""
    seed: int = 1
    """seed of the experiment"""
    torch_deterministic: bool = False
    """if toggled, `torch.backends.cudnn.deterministic=False`"""
    cuda: bool = False
    """if toggled, cuda will be enabled by default"""
    track: bool = False
    """if toggled, this experiment will be tracked with Weights and Biases"""
    wandb_project_name: str = "cleanRL"
    """the wandb's project name"""
    wandb_entity: str = None
    """the entity (team) of wandb's project"""
    capture_video: bool = False
    """whether to capture videos of the agent performances (check out `videos` folder)"""

    # Environment-specific arguments
    env_id: str = "Warehouse"
    """the id of the environment"""
    unity_env_path: str = "C:/Users/Mart9/Workspace/ABB-Warehouse/Build/ABB-RL.exe"
    """path to the Unity executable"""

    # Algorithm specific arguments
    total_timesteps: int = 60000000
    """total timesteps of the experiments"""
    learning_rate: float = 3e-4
    """the learning rate of the optimizer"""
    num_envs: int = 0
    """the number of parallel game environments. When set to 0, will be determined by unity scene."""
    num_instances: int = 6
    """the number of parallel game environments per instance"""
    num_steps: int = 256
    """the number of steps to run in each environment per policy rollout"""
    anneal_lr: bool = True
    """Toggle learning rate annealing for policy and value networks"""
    gamma: float = 0.995
    """the discount factor gamma"""
    gae_lambda: float = 0.95
    """the lambda for the general advantage estimation"""
    num_minibatches: int = 8
    """the number of mini-batches"""
    update_epochs: int = 2
    """the K epochs to update the policy"""
    norm_adv: bool = True
    """Toggles advantages normalization"""
    clip_coef: float = 0.2
    """the surrogate clipping coefficient"""
    clip_vloss: bool = True
    """Toggles whether or not to use a clipped loss for the value function, as per the paper."""
    ent_coef: float = 0.005
    """coefficient of the entropy"""
    vf_coef: float = 0.5
    """coefficient of the value function"""
    max_grad_norm: float = 0.5
    """the maximum norm for the gradient clipping"""
    target_kl: float = None
    """the target KL divergence threshold"""
    hidden_size: int = 64
    """hidden layer width for actor and critic MLPs"""
    checkpoint_interval: int = 100
    """save a training checkpoint every N PPO iterations; set to 0 to disable"""
    checkpoint_dirname: str = "checkpoints"
    """subdirectory inside the run directory used for checkpoint files"""

    # to be filled in runtime
    batch_size: int = 0
    """the batch size (computed in runtime)"""
    minibatch_size: int = 0
    """the mini-batch size (computed in runtime)"""
    num_iterations: int = 0
    """the number of iterations (computed in runtime)"""


class ModuleWrapper(nn.Module):
    def __init__(self, actor_mlp):
        super().__init__()
        self.mlp = actor_mlp

    def forward(self, x):
        return self.mlp(x)


class Agent(nn.Module):
    def __init__(self, envs: Any, args: Args):
        super().__init__()

        self.obs_dim = int(np.prod(envs.single_observation_space.shape))
        self.action_dim = int(np.prod(envs.single_action_space.shape))

        self.internal_critic_mlp = nn.Sequential(
            layer_init(nn.Linear(self.obs_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, 1), std=1.0),
        )
        self.internal_actor_mlp = nn.Sequential(
            layer_init(nn.Linear(self.obs_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, self.action_dim), std=0.01),
        )

        self.actor_mean = ModuleWrapper(self.internal_actor_mlp)
        self.critic = ModuleWrapper(self.internal_critic_mlp)
        self.actor_logstd = nn.Parameter(torch.zeros(1, self.action_dim))

    def _flatten_obs(self, x):
        return x.view(x.shape[0], -1)

    def get_value(self, x):
        return self.critic(self._flatten_obs(x))

    def get_action_and_value(self, x, action=None):
        flat_obs = self._flatten_obs(x)
        action_mean = self.internal_actor_mlp(flat_obs)
        action_logstd = self.actor_logstd.expand_as(action_mean)
        action_std = torch.exp(action_logstd)
        probs = Normal(action_mean, action_std)
        if action is None:
            action = probs.sample()
        return action, probs.log_prob(action).sum(1), probs.entropy().sum(1), self.internal_critic_mlp(flat_obs)


def run_ppo(args: Args, make_env: Callable[[Args], Any]) -> None:
    base_run_ppo(args, make_env, agent_factory=Agent)
