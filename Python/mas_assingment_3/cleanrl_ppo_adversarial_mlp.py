import os
import random
import time
from dataclasses import dataclass
from typing import Any, Callable

import gymnasium as gym
import numpy as np
import torch
import torch.nn as nn
import torch.optim as optim
from torch.distributions import Normal
from torch.utils.tensorboard import SummaryWriter

from cleanrl_ppo import layer_init
from cleanrl_ppo import (
    save_training_checkpoint,
    find_latest_checkpoint,
    load_training_checkpoint,
)
from unity_vecenv.onnx_utilities.onnx_exporter import export_unity_onnx_simple


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

    # Adversarial-specific arguments
    role_obs_index: int = -1
    """index of the boolean role flag in the flattened observation; negative indexes are supported"""
    attacker_flag_threshold: float = 0.5
    """observations with role flag value > threshold are treated as attacker samples"""
    include_role_flag_in_input: bool = False
    """if false, the role flag feature is removed before feeding each MLP"""

    # to be filled in runtime
    batch_size: int = 0
    """the batch size (computed in runtime)"""
    minibatch_size: int = 0
    """the mini-batch size (computed in runtime)"""
    num_iterations: int = 0
    """the number of iterations (computed in runtime)"""


class ModuleWrapper(nn.Module):
    def __init__(self, agent_ref):
        super().__init__()
        self._agent_ref_container = [agent_ref]

    def forward(self, x):
        return self._agent_ref_container[0]._compute_action_mean(x)


class Agent(nn.Module):
    def __init__(self, envs: Any, args: Args):
        super().__init__()

        self.obs_dim = int(np.prod(envs.single_observation_space.shape))
        self.action_dim = int(np.prod(envs.single_action_space.shape))

        self.role_obs_index = self._normalize_role_index(args.role_obs_index)
        if self.obs_dim <= 1:
            raise ValueError("Adversarial PPO requires observations with at least 2 features including role flag")

        self.input_obs_dim = self.obs_dim if args.include_role_flag_in_input else self.obs_dim - 1
        self.attacker_flag_threshold = args.attacker_flag_threshold
        self.include_role_flag_in_input = args.include_role_flag_in_input

        self.attacker_actor_mlp = nn.Sequential(
            layer_init(nn.Linear(self.input_obs_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, self.action_dim), std=0.01),
        )
        self.defender_actor_mlp = nn.Sequential(
            layer_init(nn.Linear(self.input_obs_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, self.action_dim), std=0.01),
        )
        self.attacker_critic_mlp = nn.Sequential(
            layer_init(nn.Linear(self.input_obs_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, 1), std=1.0),
        )
        self.defender_critic_mlp = nn.Sequential(
            layer_init(nn.Linear(self.input_obs_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, 1), std=1.0),
        )

        # Keep this interface for ONNX export compatibility with existing utilities.
        self.actor_mean = ModuleWrapper(self)
        self.actor_logstd = nn.Parameter(torch.zeros(2, self.action_dim))

    def _normalize_role_index(self, role_obs_index: int) -> int:
        if role_obs_index < 0:
            role_obs_index = self.obs_dim + role_obs_index
        if role_obs_index < 0 or role_obs_index >= self.obs_dim:
            raise ValueError(
                f"role_obs_index={role_obs_index} out of bounds for flattened observation size {self.obs_dim}"
            )
        return role_obs_index

    def _flatten_obs(self, x: torch.Tensor) -> torch.Tensor:
        return x.view(x.shape[0], -1)

    def _split_obs_by_role(self, flat_obs: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        role_flag = flat_obs[:, self.role_obs_index]
        is_attacker = role_flag > self.attacker_flag_threshold

        if self.include_role_flag_in_input:
            return flat_obs, is_attacker

        obs_without_flag = torch.cat(
            [
                flat_obs[:, : self.role_obs_index],
                flat_obs[:, self.role_obs_index + 1 :],
            ],
            dim=1,
        )
        return obs_without_flag, is_attacker

    def _compute_action_mean(self, x: torch.Tensor) -> torch.Tensor:
        flat_obs = self._flatten_obs(x)
        model_obs, is_attacker = self._split_obs_by_role(flat_obs)

        attacker_mean = self.attacker_actor_mlp(model_obs)
        defender_mean = self.defender_actor_mlp(model_obs)
        return torch.where(is_attacker.unsqueeze(1), attacker_mean, defender_mean)

    def get_value(self, x: torch.Tensor) -> torch.Tensor:
        flat_obs = self._flatten_obs(x)
        model_obs, is_attacker = self._split_obs_by_role(flat_obs)

        attacker_value = self.attacker_critic_mlp(model_obs)
        defender_value = self.defender_critic_mlp(model_obs)
        return torch.where(is_attacker.unsqueeze(1), attacker_value, defender_value)

    def get_action_and_value(self, x: torch.Tensor, action: torch.Tensor | None = None):
        flat_obs = self._flatten_obs(x)
        model_obs, is_attacker = self._split_obs_by_role(flat_obs)

        attacker_mean = self.attacker_actor_mlp(model_obs)
        defender_mean = self.defender_actor_mlp(model_obs)
        action_mean = torch.where(is_attacker.unsqueeze(1), attacker_mean, defender_mean)

        attacker_logstd = self.actor_logstd[0].unsqueeze(0).expand_as(action_mean)
        defender_logstd = self.actor_logstd[1].unsqueeze(0).expand_as(action_mean)
        action_logstd = torch.where(is_attacker.unsqueeze(1), attacker_logstd, defender_logstd)

        action_std = torch.exp(action_logstd)
        probs = Normal(action_mean, action_std)
        if action is None:
            action = probs.sample()

        attacker_value = self.attacker_critic_mlp(model_obs)
        defender_value = self.defender_critic_mlp(model_obs)
        value = torch.where(is_attacker.unsqueeze(1), attacker_value, defender_value)

        return action, probs.log_prob(action).sum(1), probs.entropy().sum(1), value


def run_ppo(args: Args, make_env: Callable[[Args], Any]) -> None:
    """Run PPO with differentiated logging for attacker and defender agents.
    
    Each sample is routed to the appropriate network (attacker/defender) at runtime
    based on the role flag. Separate metrics are logged for each role.
    """
    envs = make_env(args)
    args.num_envs = envs.num_envs
    args.batch_size = int(args.num_envs * args.num_steps)
    args.minibatch_size = int(args.batch_size // args.num_minibatches)
    args.num_iterations = args.total_timesteps // args.batch_size
    resume_path = args.resume_checkpoint.strip()
    if not resume_path and args.auto_resume_latest:
        auto_found = find_latest_checkpoint(args.checkpoint_dirname)
        if auto_found is not None:
            resume_path = auto_found

    is_resuming = bool(resume_path)
    if is_resuming:
        resume_path = os.path.abspath(resume_path)
        if not os.path.isfile(resume_path):
            raise FileNotFoundError(f"Resume checkpoint not found: {resume_path}")
        run_dir = os.path.dirname(os.path.dirname(resume_path))
        run_name = os.path.basename(run_dir)
    else:
        run_name = f"{args.env_id}__{args.exp_name}__{args.seed}__{int(time.time())}"
        run_dir = os.path.join("runs", run_name)
    if args.track:
        import wandb

        wandb.init(
            project=args.wandb_project_name,
            entity=args.wandb_entity,
            sync_tensorboard=True,
            config=vars(args),
            name=run_name,
            monitor_gym=True,
            save_code=True,
        )
    # TRY NOT TO MODIFY: seeding
    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    torch.backends.cudnn.deterministic = args.torch_deterministic

    device = torch.device("cuda" if torch.cuda.is_available() and args.cuda else "cpu")

    assert isinstance(envs.single_action_space, gym.spaces.Box), "only continuous action space is supported"

    agent = Agent(envs, args).to(device)
    optimizer = optim.Adam(agent.parameters(), lr=args.learning_rate, eps=1e-5)

    start_iteration = 1
    resumed_global_step = 0
    if is_resuming:
        loaded_iteration, resumed_global_step = load_training_checkpoint(resume_path, agent, optimizer, device)
        start_iteration = loaded_iteration + 1
        print(
            f"Resumed from checkpoint: {resume_path} | "
            f"last_iteration={loaded_iteration}, next_iteration={start_iteration}, global_step={resumed_global_step}"
        )

    writer = SummaryWriter(run_dir, purge_step=resumed_global_step)
    writer.add_text(
        "hyperparameters",
        "|param|value|\n|-|-|\n%s" % ("\n".join([f"|{key}|{value}|" for key, value in vars(args).items()])),
    )

    # ALGO Logic: Storage setup
    obs = torch.zeros((args.num_steps, args.num_envs) + envs.single_observation_space.shape).to(device)
    actions = torch.zeros((args.num_steps, args.num_envs) + envs.single_action_space.shape).to(device)
    logprobs = torch.zeros((args.num_steps, args.num_envs)).to(device)
    rewards = torch.zeros((args.num_steps, args.num_envs)).to(device)
    dones = torch.zeros((args.num_steps, args.num_envs)).to(device)
    values = torch.zeros((args.num_steps, args.num_envs)).to(device)
    roles = torch.zeros((args.num_steps, args.num_envs)).to(device)  # Track role for each sample

    # TRY NOT TO MODIFY: start the game
    global_step = resumed_global_step
    start_time = time.time()
    next_obs, _ = envs.reset(seed=args.seed)
    next_obs = torch.as_tensor(next_obs, dtype=torch.float32, device=device)
    next_done = torch.zeros(args.num_envs).to(device)

    STOP_FILE = "stop_training"

    last_iteration = start_iteration - 1
    for iteration in range(start_iteration, args.num_iterations + 1):
        # Stop training early : touch stop_training
        if os.path.exists(STOP_FILE):
            print("Stop signal received")
            try:
                os.remove(STOP_FILE)
            except OSError:
                pass
            save_training_checkpoint(
                agent,
                optimizer,
                iteration,
                global_step,
                run_dir,
                args,
                "stopped",
                obs_shape=envs.single_observation_space.shape,
            )
            last_iteration = iteration
            break
        # Annealing the rate if instructed to do so.
        if args.anneal_lr:
            frac = 1.0 - (iteration - 1.0) / args.num_iterations
            lrnow = frac * args.learning_rate
            optimizer.param_groups[0]["lr"] = lrnow

        sum_episode_length = 0
        sum_episode_return = 0
        episode_count = 0

        for step in range(0, args.num_steps):
            global_step += args.num_envs
            obs[step] = next_obs
            dones[step] = next_done

            # ALGO LOGIC: action logic
            with torch.no_grad():
                action, logprob, _, value = agent.get_action_and_value(next_obs)
                values[step] = value.flatten()
                
                # Extract and store role flags for later logging
                flat_obs = next_obs.view(next_obs.shape[0], -1)
                role_flag = flat_obs[:, agent.role_obs_index]
                roles[step] = role_flag
            actions[step] = action
            logprobs[step] = logprob

            # TRY NOT TO MODIFY: execute the game and log data.
            if device.type == "cuda":
                action_np = action.cpu().numpy()
            else:
                action_np = action.numpy()

            next_obs, reward, terminations, truncations, infos = envs.step(action_np)
            next_done = np.logical_or(terminations, truncations)
            rewards[step] = torch.as_tensor(reward, dtype=torch.float32, device=device).view(-1)
            next_obs = torch.as_tensor(next_obs, dtype=torch.float32, device=device)
            next_done = torch.as_tensor(next_done, dtype=torch.float32, device=device)

            if "episode" in infos:
                episode_mask = infos.get("_episode")
                if episode_mask is None:
                    episode_mask = np.ones_like(infos["episode"]["r"], dtype=bool)
                episode_mask = np.asarray(episode_mask, dtype=bool)
                if episode_mask.any():
                    sum_episode_return += float(np.asarray(infos["episode"]["r"])[episode_mask].sum())
                    sum_episode_length += int(np.asarray(infos["episode"]["l"])[episode_mask].sum())
                    episode_count += int(episode_mask.sum())

        # bootstrap value if not done
        with torch.no_grad():
            next_value = agent.get_value(next_obs).reshape(1, -1)
            advantages = torch.zeros_like(rewards).to(device)
            lastgaelam = 0
            for t in reversed(range(args.num_steps)):
                if t == args.num_steps - 1:
                    nextnonterminal = 1.0 - next_done
                    nextvalues = next_value
                else:
                    nextnonterminal = 1.0 - dones[t + 1]
                    nextvalues = values[t + 1]
                delta = rewards[t] + args.gamma * nextvalues * nextnonterminal - values[t]
                advantages[t] = lastgaelam = delta + args.gamma * args.gae_lambda * nextnonterminal * lastgaelam
            returns = advantages + values

        # flatten the batch
        b_obs = obs.reshape((-1,) + envs.single_observation_space.shape)
        b_logprobs = logprobs.reshape(-1)
        b_actions = actions.reshape((-1,) + envs.single_action_space.shape)
        b_advantages = advantages.reshape(-1)
        b_returns = returns.reshape(-1)
        b_values = values.reshape(-1)
        b_roles = roles.reshape(-1)

        # Optimizing the policy and value network
        b_inds = np.arange(args.batch_size)
        clipfracs = []
        
        # Track role-specific metrics
        attacker_losses = []
        defender_losses = []
        attacker_v_losses = []
        defender_v_losses = []
        attacker_pg_losses = []
        defender_pg_losses = []
        attacker_entropy = []
        defender_entropy = []
        
        for epoch in range(args.update_epochs):
            np.random.shuffle(b_inds)
            for start in range(0, args.batch_size, args.minibatch_size):
                end = start + args.minibatch_size
                mb_inds = b_inds[start:end]

                _, newlogprob, entropy, newvalue = agent.get_action_and_value(b_obs[mb_inds], b_actions[mb_inds])
                logratio = newlogprob - b_logprobs[mb_inds]
                ratio = logratio.exp()

                with torch.no_grad():
                    # calculate approx_kl http://joschu.net/blog/kl-approx.html
                    old_approx_kl = (-logratio).mean()
                    approx_kl = ((ratio - 1) - logratio).mean()
                    clipfracs += [((ratio - 1.0).abs() > args.clip_coef).float().mean().item()]

                mb_advantages = b_advantages[mb_inds]
                if args.norm_adv:
                    mb_advantages = (mb_advantages - mb_advantages.mean()) / (mb_advantages.std() + 1e-8)

                # Policy loss
                pg_loss1 = -mb_advantages * ratio
                pg_loss2 = -mb_advantages * torch.clamp(ratio, 1 - args.clip_coef, 1 + args.clip_coef)
                pg_loss = torch.max(pg_loss1, pg_loss2).mean()

                # Value loss
                newvalue = newvalue.view(-1)
                if args.clip_vloss:
                    v_loss_unclipped = (newvalue - b_returns[mb_inds]) ** 2
                    v_clipped = b_values[mb_inds] + torch.clamp(
                        newvalue - b_values[mb_inds],
                        -args.clip_coef,
                        args.clip_coef,
                    )
                    v_loss_clipped = (v_clipped - b_returns[mb_inds]) ** 2
                    v_loss_max = torch.max(v_loss_unclipped, v_loss_clipped)
                    v_loss = 0.5 * v_loss_max.mean()
                else:
                    v_loss = 0.5 * ((newvalue - b_returns[mb_inds]) ** 2).mean()

                entropy_loss = entropy.mean()
                loss = pg_loss - args.ent_coef * entropy_loss + v_loss * args.vf_coef

                optimizer.zero_grad()
                loss.backward()
                nn.utils.clip_grad_norm_(agent.parameters(), args.max_grad_norm)
                optimizer.step()
                
                # Track role-specific losses
                mb_roles = b_roles[mb_inds]
                is_attacker = mb_roles > args.attacker_flag_threshold
                is_defender = ~is_attacker
                
                if is_attacker.any():
                    attacker_losses.append(loss.item())
                    attacker_pg_losses.append(pg_loss.item())
                    attacker_v_losses.append(v_loss.item())
                    attacker_entropy.append(entropy_loss.item())
                if is_defender.any():
                    defender_losses.append(loss.item())
                    defender_pg_losses.append(pg_loss.item())
                    defender_v_losses.append(v_loss.item())
                    defender_entropy.append(entropy_loss.item())

            if args.target_kl is not None and approx_kl > args.target_kl:
                break

        y_pred, y_true = b_values.cpu().numpy(), b_returns.cpu().numpy()
        var_y = np.var(y_true)
        explained_var = np.nan if var_y == 0 else 1 - np.var(y_true - y_pred) / var_y

        if args.checkpoint_interval > 0 and (iteration % args.checkpoint_interval == 0 or iteration == args.num_iterations):
            checkpoint_path = save_training_checkpoint(
                agent,
                optimizer,
                iteration,
                global_step,
                run_dir,
                args,
                "checkpoint",
                obs_shape=envs.single_observation_space.shape,
            )
            print(f"Saved checkpoint: {checkpoint_path}")

        # TRY NOT TO MODIFY: record rewards for plotting purposes
        if episode_count != 0:
            writer.add_scalar("charts/avg_episodic_return", sum_episode_return / episode_count, global_step)
            writer.add_scalar("charts/avg_episodic_length", sum_episode_length / episode_count, global_step)
        writer.add_scalar("charts/learning_rate", optimizer.param_groups[0]["lr"], global_step)
        writer.add_scalar("losses/value_loss", v_loss.item(), global_step)
        writer.add_scalar("losses/policy_loss", pg_loss.item(), global_step)
        writer.add_scalar("losses/entropy", entropy_loss.item(), global_step)
        writer.add_scalar("losses/old_approx_kl", old_approx_kl.item(), global_step)
        writer.add_scalar("losses/approx_kl", approx_kl.item(), global_step)
        writer.add_scalar("losses/clipfrac", np.mean(clipfracs), global_step)
        writer.add_scalar("losses/explained_variance", explained_var, global_step)
        
        # Differentiated logging for attacker and defender
        if attacker_losses:
            writer.add_scalar("attacker/loss", np.mean(attacker_losses), global_step)
            writer.add_scalar("attacker/policy_loss", np.mean(attacker_pg_losses), global_step)
            writer.add_scalar("attacker/value_loss", np.mean(attacker_v_losses), global_step)
            writer.add_scalar("attacker/entropy", np.mean(attacker_entropy), global_step)
        
        if defender_losses:
            writer.add_scalar("defender/loss", np.mean(defender_losses), global_step)
            writer.add_scalar("defender/policy_loss", np.mean(defender_pg_losses), global_step)
            writer.add_scalar("defender/value_loss", np.mean(defender_v_losses), global_step)
            writer.add_scalar("defender/entropy", np.mean(defender_entropy), global_step)
        
        # Differentiated reward logging
        b_rewards_flat = rewards.reshape(-1).cpu().numpy()
        if len(b_rewards_flat) > 0:
            b_roles_np = b_roles.cpu().numpy()
            attacker_mask = b_roles_np > args.attacker_flag_threshold
            defender_mask = ~attacker_mask
            
            if attacker_mask.any():
                attacker_rewards = b_rewards_flat[attacker_mask]
                writer.add_scalar("attacker/avg_reward", np.mean(attacker_rewards), global_step)
            
            if defender_mask.any():
                defender_rewards = b_rewards_flat[defender_mask]
                writer.add_scalar("defender/avg_reward", np.mean(defender_rewards), global_step)
        
        avg_ep_return = (sum_episode_return / episode_count) if episode_count > 0 else float("nan")
        print("SPS:", int(global_step / (time.time() - start_time)), ", Avg Ep Return:", round(avg_ep_return, 4), ", Global Step:", global_step, round(global_step/args.total_timesteps * 100),"%, Remaining:", round((args.total_timesteps - global_step) / global_step * (time.time() - start_time) / 60), "min")
        writer.add_scalar("charts/SPS", int(global_step / (time.time() - start_time)), global_step)
        last_iteration = iteration

    final_checkpoint_path = save_training_checkpoint(
        agent,
        optimizer,
        last_iteration,
        global_step,
        run_dir,
        args,
        "final",
        obs_shape=envs.single_observation_space.shape,
    )
    print(f"Saved final checkpoint: {final_checkpoint_path}")

    # Keep the original run-level ONNX artifact as well.
    export_unity_onnx_simple(
        agent=agent,
        obs_shape=envs.single_observation_space.shape,
        out_path=os.path.join(run_dir, f"{run_name}_policy.onnx"),
        opset=13,
    )

    envs.close()
    writer.close()
