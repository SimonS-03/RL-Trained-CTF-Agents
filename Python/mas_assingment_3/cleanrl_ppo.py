# docs and experiment results can be found at https://docs.cleanrl.dev/rl-algorithms/ppo/#ppopy
import random
import time
from dataclasses import dataclass
from typing import Any, Callable
import os

import gymnasium as gym
import numpy as np
import torch
import torch.nn as nn
import torch.optim as optim
from torch.distributions import Normal
from torch.utils.tensorboard import SummaryWriter

from unity_vecenv.onnx_utilities.onnx_exporter import export_unity_onnx_simple


@dataclass(frozen=True)
class AttentionSpec:
    name: str
    start: int
    count: int
    item_dim: int

    @property
    def end(self) -> int:
        return self.start + self.count * self.item_dim

    @property
    def raw_dim(self) -> int:
        return self.count * self.item_dim


@dataclass
class Args:
    exp_name: str = "ppo"
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
    enemy_encoder: str = "attention"
    """encoder for enemies: attention | deepsets"""
    food_encoder: str = "deepsets"
    """encoder for food: attention | deepsets"""
    friendly_encoder: str = "deepsets"
    """encoder for friendlies: attention | deepsets"""
    enemy_embed_dim: int = 64
    """embedding size for enemy entities"""
    food_embed_dim: int = 16
    """embedding size for food entities"""
    friendly_embed_dim: int = 32
    """embedding size for friendly entities"""
    attention_heads: int = 2
    """maximum number of heads used by attention encoders"""
    checkpoint_interval: int = 100
    """save a training checkpoint every N PPO iterations; set to 0 to disable"""
    checkpoint_dirname: str = "checkpoints"
    """subdirectory inside the run directory used for checkpoint files"""
    resume_checkpoint: str = ""
    """path to a .pt checkpoint file to resume training from"""
    auto_resume_latest: bool = False
    """if toggled, resume from the latest checkpoint in runs/*/checkpoints"""

    # to be filled in runtime
    batch_size: int = 0
    """the batch size (computed in runtime)"""
    minibatch_size: int = 0
    """the mini-batch size (computed in runtime)"""
    num_iterations: int = 0
    """the number of iterations (computed in runtime)"""


def layer_init(layer: nn.Module, std: float = np.sqrt(2), bias_const: float = 0.0) -> nn.Module:
    torch.nn.init.orthogonal_(layer.weight, std)
    torch.nn.init.constant_(layer.bias, bias_const)
    return layer


def save_training_checkpoint(
    agent: nn.Module,
    optimizer: optim.Optimizer,
    iteration: int,
    global_step: int,
    run_dir: str,
    args: Args,
    tag: str,
    obs_shape: tuple[int, ...] | None = None,
) -> str:
    checkpoint_dir = os.path.join(run_dir, args.checkpoint_dirname)
    os.makedirs(checkpoint_dir, exist_ok=True)
    checkpoint_path = os.path.join(checkpoint_dir, f"{tag}_{iteration:06d}_step_{global_step:09d}.pt")
    torch.save(
        {
            "iteration": iteration,
            "global_step": global_step,
            "model_state_dict": agent.state_dict(),
            "optimizer_state_dict": optimizer.state_dict(),
            "args": vars(args),
            "tag": tag,
        },
        checkpoint_path,
    )

    # Save an ONNX sidecar with the same base name for easier deployment from checkpoints.
    if obs_shape is not None:
        onnx_path = os.path.splitext(checkpoint_path)[0] + ".onnx"
        try:
            export_unity_onnx_simple(
                agent=agent,
                obs_shape=obs_shape,
                out_path=onnx_path,
                opset=13,
            )
        except Exception as exc:
            print(f"Warning: failed to export ONNX for checkpoint {checkpoint_path}: {exc}")

    return checkpoint_path


def find_latest_checkpoint(checkpoint_dirname: str) -> str | None:
    if not os.path.isdir("runs"):
        return None

    latest_path = None
    latest_mtime = -1.0
    for run_name in os.listdir("runs"):
        run_dir = os.path.join("runs", run_name)
        if not os.path.isdir(run_dir):
            continue
        ckpt_dir = os.path.join(run_dir, checkpoint_dirname)
        if not os.path.isdir(ckpt_dir):
            continue
        for filename in os.listdir(ckpt_dir):
            if not filename.endswith(".pt"):
                continue
            path = os.path.join(ckpt_dir, filename)
            mtime = os.path.getmtime(path)
            if mtime > latest_mtime:
                latest_mtime = mtime
                latest_path = path
    return latest_path


def load_training_checkpoint(
    checkpoint_path: str,
    agent: nn.Module,
    optimizer: optim.Optimizer,
    device: torch.device,
) -> tuple[int, int]:
    checkpoint = torch.load(checkpoint_path, map_location=device)
    agent.load_state_dict(checkpoint["model_state_dict"])
    optimizer.load_state_dict(checkpoint["optimizer_state_dict"])
    iteration = int(checkpoint.get("iteration", 0))
    global_step = int(checkpoint.get("global_step", 0))
    return iteration, global_step

class ModuleWrapper(nn.Module):
    def __init__(self, agent_ref, actor_mlp):
        super().__init__()
        # Store a reference to the agent's entity processing logic.
        self.mlp = actor_mlp
        self._agent_ref_container = [agent_ref]

    def forward(self, x):
        # This is what will be recorded in the ONNX file.
        processed_x = self._agent_ref_container[0]._process_attention(x)
        return self.mlp(processed_x)

class Agent(nn.Module):

    """
    PACMAN agent with attention inputs for nearby enemies, food, and friendly agents.

    Observations expected from Unity (PacManRLAgent.CollectObservation):
    - Ego and map context: 18 dims
    - Shared visible enemies: K=4, dim=8 each
    - Food observations: K=6, dim=4 each
    - Friendly observations: K=3, dim=8 each

    Flattened index map:
    - [0:18]   ego/map context
    - [18:50]  enemies (4 x 8)
    - [50:74]  food (6 x 4)
    - [74:98]  friendlies (3 x 8)
    """

    # Observation layout used by Unity PacManRLAgent. Keep these values in sync with C#.
    EGO_CONTEXT_DIM = 5 # Added to each entity observation for context: ghost, scared, score, carried food, flow cost
    EGO_QUERY_DIM = 18
    K_NEAREST = 4
    DIMS_PER_ENEMY = 8
    ENEMY_START = 18 
    ENEMY_END = ENEMY_START + K_NEAREST * DIMS_PER_ENEMY
    K_NEAREST_FOOD = 6
    DIMS_PER_FOOD = 4
    FOOD_START = ENEMY_END
    FOOD_END = FOOD_START + K_NEAREST_FOOD * DIMS_PER_FOOD
    K_NEAREST_FRIENDLY = 3
    DIMS_PER_FRIENDLY = 8
    FRIENDLY_START = FOOD_END
    FRIENDLY_END = FRIENDLY_START + K_NEAREST_FRIENDLY * DIMS_PER_FRIENDLY

    def __init__(self, envs: Any, args: Args):
        super().__init__()

        self.raw_obs_dims = envs.single_observation_space.shape
        self._attention_specs = (
            AttentionSpec("enemies", self.ENEMY_START, self.K_NEAREST, self.DIMS_PER_ENEMY),
            AttentionSpec("food", self.FOOD_START, self.K_NEAREST_FOOD, self.DIMS_PER_FOOD),
            AttentionSpec("friendlies", self.FRIENDLY_START, self.K_NEAREST_FRIENDLY, self.DIMS_PER_FRIENDLY),
        )
        self.enemy_spec, self.food_spec, self.friendly_spec = self._attention_specs

        self.encoder_modes = {
            "enemies": args.enemy_encoder.lower(),
            "food": args.food_encoder.lower(),
            "friendlies": args.friendly_encoder.lower(),
        }
        valid_modes = {"attention", "deepsets"}
        for name, mode in self.encoder_modes.items():
            if mode not in valid_modes:
                raise ValueError(f"Unsupported encoder mode '{mode}' for {name}. Expected one of {sorted(valid_modes)}")

        self.embed_dims = {
            "enemies": args.enemy_embed_dim,
            "food": args.food_embed_dim,
            "friendlies": args.friendly_embed_dim,
        }

        # Entity encoders are separate because food, enemies, and allies use different raw features.
        self.entity_encoders = nn.ModuleDict({
            spec.name: self._build_entity_encoder(spec.item_dim, self.embed_dims[spec.name])
            for spec in self._attention_specs
        })

        self.ego_query_encoders = nn.ModuleDict()
        self.query_norms = nn.ModuleDict()
        self.entity_norms = nn.ModuleDict()
        self.attentions = nn.ModuleDict()
        for spec in self._attention_specs:
            if self.encoder_modes[spec.name] != "attention":
                continue

            embed_dim = self.embed_dims[spec.name]
            num_heads = self._resolve_attention_heads(embed_dim, args.attention_heads)
            self.ego_query_encoders[spec.name] = nn.Sequential(
                layer_init(nn.Linear(self.EGO_QUERY_DIM, embed_dim)),
                nn.ReLU(),
            )
            self.query_norms[spec.name] = nn.LayerNorm(embed_dim)
            self.entity_norms[spec.name] = nn.LayerNorm(embed_dim)
            self.attentions[spec.name] = nn.MultiheadAttention(embed_dim=embed_dim, num_heads=num_heads, batch_first=True)

        remaining_obs_dim = self.EGO_QUERY_DIM
        encoded_summary_dim = sum(self.embed_dims[spec.name] for spec in self._attention_specs)
        input_dim = remaining_obs_dim + encoded_summary_dim
        self.internal_critic_mlp = nn.Sequential(
            layer_init(nn.Linear(input_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, 1), std=1.0),
        )
        self.internal_actor_mlp = nn.Sequential(
            layer_init(nn.Linear(input_dim, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, args.hidden_size)),
            nn.Tanh(),
            layer_init(nn.Linear(args.hidden_size, np.prod(envs.single_action_space.shape)), std=0.01),
        )

        # ONNX export searches for this specific function so all the logic is wrapped inside it.
        self.actor_mean = ModuleWrapper(self, self.internal_actor_mlp)
        self.critic = ModuleWrapper(self, self.internal_critic_mlp)

        self.actor_logstd = nn.Parameter(torch.zeros(1, np.prod(envs.single_action_space.shape)))

    @staticmethod
    def _resolve_attention_heads(embed_dim: int, requested_heads: int) -> int:
        if requested_heads <= 1:
            return 1

        for heads in range(min(requested_heads, embed_dim), 0, -1):
            if embed_dim % heads == 0:
                return heads
        return 1

    def _build_entity_encoder(self, raw_feature_dim: int, embed_dim: int) -> nn.Sequential:
        return nn.Sequential(
            layer_init(nn.Linear(raw_feature_dim + self.EGO_CONTEXT_DIM, embed_dim)),
            nn.ReLU(),
            layer_init(nn.Linear(embed_dim, embed_dim)),
            nn.ReLU(),
        )

    def _extract_entity_context(self, x):
        return x[:, :self.EGO_CONTEXT_DIM]

    def _remove_attention_segments(self, x):
        # The non-attention context is the fixed ego/map prefix in this layout.
        return x[:, :self.EGO_QUERY_DIM]

    def _process_attention_block(self, x, spec: AttentionSpec):
        entity_raw = x[:, spec.start:spec.end].view(-1, spec.count, spec.item_dim)
        ego_ctx = self._extract_entity_context(x)

        ego_repeated = ego_ctx.unsqueeze(1).expand(-1, spec.count, -1)
        entity_combined = torch.cat([entity_raw, ego_repeated], dim=-1)
        encoded_entities = self.entity_encoders[spec.name](entity_combined)
        padding_mask = entity_raw.abs().sum(dim=-1) == 0

        if self.encoder_modes[spec.name] == "deepsets":
            valid_mask = (~padding_mask).unsqueeze(-1).float()
            summed = (encoded_entities * valid_mask).sum(dim=1)
            counts = valid_mask.sum(dim=1).clamp(min=1.0)
            return summed / counts

        ego_raw = x[:, :self.EGO_QUERY_DIM]
        query = self.query_norms[spec.name](self.ego_query_encoders[spec.name](ego_raw)).unsqueeze(1)
        key_value = self.entity_norms[spec.name](encoded_entities)

        all_masked = padding_mask.all(dim=-1, keepdim=True)
        padding_mask = torch.cat([
            padding_mask[:, :1] & ~all_masked,
            padding_mask[:, 1:],
        ], dim=1)

        attn_output, _ = self.attentions[spec.name](
            query=query,
            key=key_value,
            value=key_value,
            key_padding_mask=padding_mask,
        )
        return attn_output.squeeze(1)

    def _process_attention(self, x):
        enemy_summary = self._process_attention_block(x, self.enemy_spec)
        food_summary = self._process_attention_block(x, self.food_spec)
        friendly_summary = self._process_attention_block(x, self.friendly_spec)
        remaining_obs = self._remove_attention_segments(x)

        return torch.cat([remaining_obs, enemy_summary, food_summary, friendly_summary], dim=-1)

    def get_value(self, x):
        # x = self._process_enemies(x)
        return self.critic(x)

    def get_action_and_value(self, x, action=None):
        processed_x = self._process_attention(x)
        # action_mean = self.actor_mean(x) # actor_mean handles enemy processing in the wrapper
        action_mean = self.internal_actor_mlp(processed_x)
        action_logstd = self.actor_logstd.expand_as(action_mean)
        action_std = torch.exp(action_logstd)
        probs = Normal(action_mean, action_std)
        if action is None:
            action = probs.sample()
        return action, probs.log_prob(action).sum(1), probs.entropy().sum(1), self.internal_critic_mlp(processed_x)



def run_ppo(args: Args, make_env: Callable[[Args], Any], agent_factory: Callable[[Any, Args], nn.Module] = Agent) -> None:
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

    agent = agent_factory(envs, args).to(device)
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

        # Optimizing the policy and value network
        b_inds = np.arange(args.batch_size)
        clipfracs = []
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
